#nullable enable
using AspNetCore.Localizer.Json.Extensions;
using AspNetCore.Localizer.Json.Format;
using Microsoft.Extensions.Localization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using AspNetCore.Localizer.Json.JsonOptions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

namespace AspNetCore.Localizer.Json.Localizer
{
    internal partial class JsonStringLocalizer : JsonStringLocalizerBase, IJsonStringLocalizer
    {
        private readonly Dictionary<string, IDictionary<string, string>> _missingJsonValues = new();
        private readonly string _missingTranslations = null;

        public JsonStringLocalizer(IOptions<JsonLocalizationOptions> localizationOptions) : base(localizationOptions)
        {
            _missingTranslations = localizationOptions.Value.MissingTranslationsOutputFile;
        }

        public JsonStringLocalizer(IOptions<JsonLocalizationOptions> localizationOptions, string baseName) : base(
            localizationOptions, baseName)
        {
            _missingTranslations = localizationOptions.Value.MissingTranslationsOutputFile;
        }

        private static LocalizedString ConvertToChar(string value, char c, int additionalRepeats = 0) =>
            new LocalizedString(value, new string(c, value.Length + additionalRepeats));

        public LocalizedString this[string name] => GetLocalizedString(name);

        public LocalizedString this[string name, params object[] arguments] => GetLocalizedString(name, arguments);

        private LocalizedString GetLocalizedString(string name, params object[] arguments)
        {
            if (LocalizationOptions.Value.LocalizerDiagnosticMode)
            {
                return ConvertToChar(name, 'X');
            }

            string? value = GetString(name);
            bool resourceNotFound = value == null;

            value ??= name; // Utilise le nom comme valeur par défaut si aucune valeur n'est trouvée

            value = FormatString(value, arguments);

            return new LocalizedString(name, value, resourceNotFound);
        }


        private string FormatString(string value, object[]? arguments)
        {
            if (arguments is { Length: > 0 })
            {
                value = string.Format(value, arguments);

                if (arguments[^1] is bool isPlural)
                {
                    if (!string.IsNullOrEmpty(value) && value.Contains(LocalizationOptions.Value.PluralSeparator))
                    {
                        var parts = value.Split(new[] { LocalizationOptions.Value.PluralSeparator }, 2,
                            StringSplitOptions.None);
                        return parts.Length > 1 ? parts[isPlural ? 1 : 0] : value;
                    }
                }
            }

            return value;
        }

        public LocalizedString GetPlural(string name, double count, params object[] arguments)
        {
            InitCorrectJsonCulture();

            IPluralizationRuleSet pluralizationRuleSet = GetPluralizationToUse();
            string applicableRule = pluralizationRuleSet.GetMatchingPluralizationRule(count);

            string nameWithRule = $"{name}.{applicableRule}";

            if (Localization != null && Localization.TryGetValue(nameWithRule, out var localizedValue))
            {
                return FormatLocalizedString(name, localizedValue.Value, count, arguments);
            }

            string nameWithOtherRule = $"{name}.{PluralizationConstants.Other}";
            if (Localization != null && Localization.TryGetValue(nameWithOtherRule, out var localizedOtherValue))
            {
                return FormatLocalizedString(name, localizedOtherValue.Value, count, arguments);
            }

            string fallback = GetString(name, true);
            return FormatLocalizedString(name, fallback ?? name, count, arguments, fallback == null);
        }


        private LocalizedString FormatLocalizedString(string name, string format, double count, object[] arguments,
            bool resourceNotFound = false)
        {
            // unshift the count to the arguments array
            object[] argumentsWithCount = new object[arguments.Length + 1];
            argumentsWithCount[0] = count;

            // copy the rest of the arguments
            Array.Copy(arguments, 0, argumentsWithCount, 1, arguments.Length);

            // format the string
            string value = string.Format(format, argumentsWithCount);
            return new LocalizedString(name, value, resourceNotFound);
        }

        /// <summary>
        /// Initializes the JSON localizer for the current request's culture.
        /// </summary>
        /// <param name="shouldTryDefaultCulture">Whether to try the default culture as a fallback.</param>
        /// <returns>The <see cref="CultureInfo"/> being used for localization.</returns>
        /// <remarks>
        /// <para>
        /// In ASP.NET Core, the <see cref="CultureInfo.CurrentUICulture"/> is set per-request by the
        /// <c>RequestLocalizationMiddleware</c> based on the Accept-Language header or other configured
        /// providers. This is the correct culture to use for localizing HTTP responses.
        /// </para>
        /// <para>
        /// The previous implementation used <c>DefaultThreadCurrentUICulture ?? CurrentUICulture</c>,
        /// which caused localization to fail in exception handlers, integration tests, and other
        /// scenarios where <c>DefaultThreadCurrentUICulture</c> was set to the system default (e.g., en-US)
        /// and overrode the per-request culture set by the middleware.
        /// </para>
        /// <para>
        /// See: https://github.com/AskmethatFR/AspNetCore.Localizer.Json/issues/XXX
        /// </para>
        /// </remarks>
        private CultureInfo InitCorrectJsonCulture(bool shouldTryDefaultCulture = false)
        {
            // IMPORTANT: Use CurrentUICulture (set per-request by ASP.NET Core's RequestLocalizationMiddleware)
            // instead of DefaultThreadCurrentUICulture (process-wide default that incorrectly overrides
            // the per-request culture in exception handlers, test hosts, and other edge cases).
            var culture = CultureInfo.CurrentUICulture;

            if (!IsUiCultureCurrentCulture(culture))
            {
                InitJsonFromCulture(culture);
            }

            return culture;
        }

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
        {
            InitCorrectJsonCulture();

            return Localization?.Where(w => includeParentCultures || !w.Value.IsParent)
                .Select(l =>
                    new LocalizedString(l.Key, l.Value.Value ?? l.Key, resourceNotFound: l.Value.Value == null))
                .OrderBy(s => s.Name);
        }

        public IStringLocalizer WithCulture(CultureInfo culture)
        {
            LocalizationOptions.Value.SupportedCultureInfos.Add(culture);

            CultureInfo.CurrentCulture = culture;

            return new JsonStringLocalizer(LocalizationOptions);
        }

        /// <summary>
        /// Per-instance cache for translated strings. The key includes culture name to ensure
        /// culture changes return the correct translation.
        /// Format: "{cultureName}:{resourceKey}"
        /// </summary>
        private readonly Dictionary<string, string> _localStringCache = new();

        /// <summary>
        /// Builds a culture-aware cache key for the local string cache.
        /// </summary>
        /// <param name="cultureName">The culture name (e.g., "en-US", "nb-NO").</param>
        /// <param name="name">The resource key name.</param>
        /// <returns>A composite cache key in the format "{cultureName}:{name}".</returns>
        private static string GetLocalCacheKey(string cultureName, string name) => $"{cultureName}:{name}";

        private string? GetString(string name, bool shouldTryDefaultCulture = true)
        {
            if (string.IsNullOrEmpty(name))
            {
                Console.Error.WriteLine(
                $"You are trying to locate an empty string, please verify");
                return string.Empty;
            }

            // IMPORTANT: Determine culture FIRST, before checking cache.
            // This ensures we use the correct culture-specific cache key.
            var culture = InitCorrectJsonCulture(true);
            var cacheKey = GetLocalCacheKey(culture.Name, name);

            // Check cache with culture-aware key
            if (_localStringCache.TryGetValue(cacheKey, out string? cachedValue))
            {
                return cachedValue;
            }

            if (Localization != null && Localization.TryGetValue(name, out LocalizatedFormat? localizedValue))
            {
                _localStringCache[cacheKey] = localizedValue.Value;
                return localizedValue.Value;
            }

            if (shouldTryDefaultCulture)
            {
                InitJsonFromCulture(LocalizationOptions.Value.DefaultCulture);
                return GetString(name, false);
            }

            HandleMissingTranslation(name, culture);
            return null;
        }


        private void HandleMissingTranslation(string name, CultureInfo? culture)
        {
            var cultureName = culture?.TwoLetterISOLanguageName ?? "default";

            if (LocalizationOptions.Value.MissingTranslationLogBehavior ==
                MissingTranslationLogBehavior.LogConsoleError)
            {
                Console.Error.WriteLine($"'{name}' does not contain any translation for {cultureName}");
            }

            if (LocalizationOptions.Value.MissingTranslationLogBehavior == MissingTranslationLogBehavior.CollectToJSON)
            {
                if (!_missingJsonValues.TryGetValue(cultureName, out var localeMissingValues))
                {
                    localeMissingValues = new Dictionary<string, string>();
                    _missingJsonValues.TryAdd(cultureName, localeMissingValues);
                }

                if (localeMissingValues.TryAdd(name, name))
                {
                    Console.Error.WriteLine($"'{name}' added to missing values");
                    WriteMissingTranslations();
                }
            }
        }

        public MarkupString GetHtmlBlazorString(string name, bool shouldTryDefaultCulture = true) =>
            new MarkupString(GetString(name, shouldTryDefaultCulture));

        private void InitJsonFromCulture(CultureInfo cultureInfo)
        {
            var isFromMemCache = InitJsonStringLocalizer(cultureInfo);
            if (!isFromMemCache)
            {
                AddMissingCultureToSupportedCulture(cultureInfo);
                GetCultureToUse(cultureInfo);
            }
        }

        public void ClearMemCache(IEnumerable<CultureInfo> culturesToClearFromCache = null)
        {
            foreach (var cultureInfo in culturesToClearFromCache ??
                                        LocalizationOptions.Value.SupportedCultureInfos.ToArray())
                MemCache.Remove(GetCacheKey(cultureInfo));
        }

        public void ReloadMemCache(IEnumerable<CultureInfo> reloadCulturesToCache = null)
        {
            ClearMemCache();
            foreach (var cultureInfo in reloadCulturesToCache ??
                                        LocalizationOptions.Value.SupportedCultureInfos.ToArray())
                InitJsonFromCulture(cultureInfo);
        }

        private void WriteMissingTranslations()
        {
            if (string.IsNullOrWhiteSpace(_missingTranslations) || _missingJsonValues.Count == 0)
                return;

            try
            {
                lock (_missingJsonValues)
                {
                    foreach (var locale in _missingJsonValues)
                    {
                        if (locale.Value.Count == 0)
                        {
                            continue;
                        }

                        var json = JsonSerializer.Serialize(locale.Value);
                        var newFile = Path.ChangeExtension(
                            $"{Path.GetFileNameWithoutExtension(_missingTranslations)}-{locale.Key}",
                            Path.GetExtension(_missingTranslations)
                        );

                        File.WriteAllText(newFile, json);
                        Console.Error.WriteLine(
                            $"Writing {locale.Value.Count} missing translations to {Path.GetFullPath(newFile)}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Cannot write missing translations to {_missingTranslations}: {ex.Message}");
            }
        }
    }
}