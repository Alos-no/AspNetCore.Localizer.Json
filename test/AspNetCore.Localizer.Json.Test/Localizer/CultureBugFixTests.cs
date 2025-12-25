using AspNetCore.Localizer.Json.Localizer;
using AspNetCore.Localizer.Json.Test.Helpers;
using Microsoft.Extensions.Localization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using AspNetCore.Localizer.Json.JsonOptions;

namespace AspNetCore.Localizer.Json.Test.Localizer
{
    /// <summary>
    /// Tests for critical bug fixes in the JSON localizer:
    /// 1. Cache key missing culture - cache was keyed by resource name only, not culture
    /// 2. Culture resolution priority - DefaultThreadCurrentUICulture incorrectly overrode CurrentUICulture
    /// 3. Fallback cache state corruption - after fallback to default culture on cache HIT,
    ///    _currentCulture was not updated, causing subsequent lookups to use wrong culture
    /// </summary>
    [TestClass]
    public class CultureBugFixTests
    {
        private JsonStringLocalizer CreateLocalizer()
        {
            return JsonStringLocalizerHelperFactory.Create(new JsonLocalizationOptions()
            {
                DefaultCulture = new CultureInfo("en-US"),
                SupportedCultureInfos = new System.Collections.Generic.HashSet<CultureInfo>()
                {
                    new CultureInfo("en-US"),
                    new CultureInfo("nb-NO"),
                    new CultureInfo("fr-FR"),
                    new CultureInfo("de-DE"),
                },
                ResourcesPath = "culturefix",
                AssemblyHelper = new AssemblyStub(Assembly.GetCallingAssembly())
            });
        }

        #region Bug Fix 1: Cache Key Missing Culture

        /// <summary>
        /// Tests that the same resource key returns correct translations when switching cultures.
        ///
        /// Bug description: The _localStringCache was keyed by resource name only (e.g., "Greeting"),
        /// not by culture. Once a string was cached in one culture (e.g., en-US returning "Hello"),
        /// subsequent requests for any culture would return the cached value.
        ///
        /// Fix: Changed cache key to include culture name: "{cultureName}:{resourceName}"
        /// </summary>
        [TestMethod]
        public void Should_Return_Correct_Translation_When_Switching_Cultures()
        {
            // Arrange
            var localizer = CreateLocalizer();

            // Act - First request in English
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            LocalizedString resultEnglish = localizer.GetString("Greeting");

            // Act - Switch to Norwegian
            CultureInfo.CurrentUICulture = new CultureInfo("nb-NO");
            LocalizedString resultNorwegian = localizer.GetString("Greeting");

            // Act - Switch to French
            CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");
            LocalizedString resultFrench = localizer.GetString("Greeting");

            // Assert - Each culture should return its own translation
            Assert.AreEqual("Hello", resultEnglish.Value, "English translation should be 'Hello'");
            Assert.AreEqual("Hei", resultNorwegian.Value, "Norwegian translation should be 'Hei'");
            Assert.AreEqual("Bonjour", resultFrench.Value, "French translation should be 'Bonjour'");
        }

        /// <summary>
        /// Tests that switching cultures back and forth returns correct cached values.
        /// This verifies the cache correctly maintains separate entries per culture.
        /// </summary>
        [TestMethod]
        public void Should_Return_Cached_Value_Per_Culture_When_Switching_Back_And_Forth()
        {
            // Arrange
            var localizer = CreateLocalizer();

            // Act - First pass: cache values in all cultures
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            var english1 = localizer.GetString("Farewell");

            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            var german1 = localizer.GetString("Farewell");

            // Act - Second pass: switch back and verify cache returns correct values
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            var english2 = localizer.GetString("Farewell");

            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            var german2 = localizer.GetString("Farewell");

            // Assert
            Assert.AreEqual("Goodbye", english1.Value);
            Assert.AreEqual("Auf Wiedersehen", german1.Value);
            Assert.AreEqual("Goodbye", english2.Value, "Second English request should return cached 'Goodbye'");
            Assert.AreEqual("Auf Wiedersehen", german2.Value, "Second German request should return cached 'Auf Wiedersehen'");
        }

        /// <summary>
        /// Tests multiple different keys across different cultures to ensure cache isolation.
        /// </summary>
        [TestMethod]
        public void Should_Correctly_Cache_Multiple_Keys_Across_Cultures()
        {
            // Arrange
            var localizer = CreateLocalizer();

            // Act - Request multiple keys in French
            CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");
            var frenchGreeting = localizer.GetString("Greeting");
            var frenchFarewell = localizer.GetString("Farewell");
            var frenchWelcome = localizer.GetString("Welcome");

            // Act - Request same keys in Norwegian
            CultureInfo.CurrentUICulture = new CultureInfo("nb-NO");
            var norwegianGreeting = localizer.GetString("Greeting");
            var norwegianFarewell = localizer.GetString("Farewell");
            var norwegianWelcome = localizer.GetString("Welcome");

            // Assert - French values
            Assert.AreEqual("Bonjour", frenchGreeting.Value);
            Assert.AreEqual("Au revoir", frenchFarewell.Value);
            Assert.AreEqual("Bienvenue dans notre application", frenchWelcome.Value);

            // Assert - Norwegian values (should not be French cached values)
            Assert.AreEqual("Hei", norwegianGreeting.Value);
            Assert.AreEqual("Ha det", norwegianFarewell.Value);
            Assert.AreEqual("Velkommen til applikasjonen var", norwegianWelcome.Value);
        }

        #endregion

        #region Bug Fix 2: Culture Resolution Priority

        /// <summary>
        /// Tests that CurrentUICulture is used for localization, not DefaultThreadCurrentUICulture.
        ///
        /// Bug description: The library used DefaultThreadCurrentUICulture ?? CurrentUICulture,
        /// which meant the process-wide default (often set to en-US) overrode the per-request
        /// culture set by ASP.NET Core's RequestLocalizationMiddleware.
        ///
        /// Fix: Now uses CurrentUICulture directly, which is correctly set per-request by middleware.
        /// </summary>
        [TestMethod]
        public void Should_Use_CurrentUICulture_Not_DefaultThreadCurrentUICulture()
        {
            // Arrange
            var localizer = CreateLocalizer();

            // Save original values
            var originalDefaultUICulture = CultureInfo.DefaultThreadCurrentUICulture;
            var originalCurrentUICulture = CultureInfo.CurrentUICulture;

            try
            {
                // Simulate a scenario where DefaultThreadCurrentUICulture is set to en-US
                // but the request culture (CurrentUICulture) is Norwegian
                CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");
                CultureInfo.CurrentUICulture = new CultureInfo("nb-NO");

                // Act
                LocalizedString result = localizer.GetString("Greeting");

                // Assert - Should return Norwegian, not English
                Assert.AreEqual("Hei", result.Value,
                    "Should use CurrentUICulture (nb-NO), not DefaultThreadCurrentUICulture (en-US)");
            }
            finally
            {
                // Restore original values
                CultureInfo.DefaultThreadCurrentUICulture = originalDefaultUICulture;
                CultureInfo.CurrentUICulture = originalCurrentUICulture;
            }
        }

        /// <summary>
        /// Tests that per-request culture changes are respected even when DefaultThreadCurrentUICulture is set.
        /// This simulates the ASP.NET Core middleware behavior where each request can have different culture.
        /// </summary>
        [TestMethod]
        public void Should_Respect_Per_Request_Culture_Changes()
        {
            // Arrange
            var localizer = CreateLocalizer();
            var originalDefaultUICulture = CultureInfo.DefaultThreadCurrentUICulture;
            var originalCurrentUICulture = CultureInfo.CurrentUICulture;

            try
            {
                // Set a process-wide default that should NOT affect per-request localization
                CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

                // Simulate multiple requests with different cultures
                // Request 1: French user
                CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");
                var request1Result = localizer.GetString("Greeting");

                // Request 2: German user
                CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
                var request2Result = localizer.GetString("Greeting");

                // Request 3: Norwegian user
                CultureInfo.CurrentUICulture = new CultureInfo("nb-NO");
                var request3Result = localizer.GetString("Greeting");

                // Assert - Each "request" should get its own culture's translation
                Assert.AreEqual("Bonjour", request1Result.Value, "French request should get French translation");
                Assert.AreEqual("Hallo", request2Result.Value, "German request should get German translation");
                Assert.AreEqual("Hei", request3Result.Value, "Norwegian request should get Norwegian translation");
            }
            finally
            {
                CultureInfo.DefaultThreadCurrentUICulture = originalDefaultUICulture;
                CultureInfo.CurrentUICulture = originalCurrentUICulture;
            }
        }

        /// <summary>
        /// Tests that the localizer works correctly in an "exception handler" scenario
        /// where DefaultThreadCurrentUICulture might be set but per-request culture should still be used.
        /// This was a common failure scenario before the fix.
        /// </summary>
        [TestMethod]
        public void Should_Work_In_Exception_Handler_Scenario()
        {
            // Arrange
            var localizer = CreateLocalizer();
            var originalDefaultUICulture = CultureInfo.DefaultThreadCurrentUICulture;
            var originalCurrentUICulture = CultureInfo.CurrentUICulture;

            try
            {
                // Simulate exception handler scenario:
                // - DefaultThreadCurrentUICulture set to system default (en-US)
                // - But the original request was from a French user
                CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");
                CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");

                // Simulate exception being thrown and caught
                try
                {
                    throw new System.Exception("Test exception");
                }
                catch
                {
                    // In exception handler, localize error message
                    // CurrentUICulture should still be French from the original request
                    var errorMessage = localizer.GetString("Greeting");

                    Assert.AreEqual("Bonjour", errorMessage.Value,
                        "Exception handler should still use the request's culture (French)");
                }
            }
            finally
            {
                CultureInfo.DefaultThreadCurrentUICulture = originalDefaultUICulture;
                CultureInfo.CurrentUICulture = originalCurrentUICulture;
            }
        }

        #endregion

        #region Bug Fix 3: Cache State Corruption on Culture Switch

        /// <summary>
        /// Tests that switching between cached cultures maintains correct Localization state.
        ///
        /// Bug description: In InitJsonStringLocalizer, on cache HIT, Localization is set
        /// via the out parameter of MemCache.TryGetValue, but _currentCulture is NOT updated.
        /// This causes IsUiCultureCurrentCulture to return TRUE on subsequent calls even though
        /// the Localization dictionary is pointing to a different culture's translations.
        ///
        /// Scenario:
        /// 1. Request in en-US → caches en-US blob, _currentCulture = "en-US"
        /// 2. Request in nb-NO → caches nb-NO blob, _currentCulture = "nb-NO"
        /// 3. Request in en-US → cache HIT, Localization = en-US blob, BUT _currentCulture stays "nb-NO"
        /// 4. Request in nb-NO → IsUiCultureCurrentCulture("nb-NO") returns TRUE (wrong!)
        ///    → No reload happens, but Localization still points to en-US blob!
        ///    → Returns WRONG culture's translations!
        ///
        /// Fix: Update _currentCulture in InitJsonStringLocalizer on cache HIT.
        /// </summary>
        [TestMethod]
        public void Should_Return_Correct_Culture_After_Switching_Back_To_Cached_Culture()
        {
            // Arrange
            var localizer = CreateLocalizer();
            var originalCurrentUICulture = CultureInfo.CurrentUICulture;

            try
            {
                // Step 1: Request in English - caches en-US blob, _currentCulture = "en-US"
                CultureInfo.CurrentUICulture = new CultureInfo("en-US");
                var english1 = localizer.GetString("Greeting");
                Assert.AreEqual("Hello", english1.Value, "First English request should return 'Hello'");

                // Step 2: Request in Norwegian - caches nb-NO blob, _currentCulture = "nb-NO"
                CultureInfo.CurrentUICulture = new CultureInfo("nb-NO");
                var norwegian1 = localizer.GetString("Greeting");
                Assert.AreEqual("Hei", norwegian1.Value, "First Norwegian request should return 'Hei'");

                // Step 3: Request in English again - cache HIT for en-US
                // BUG: Localization switches to en-US blob but _currentCulture stays "nb-NO"
                CultureInfo.CurrentUICulture = new CultureInfo("en-US");
                var english2 = localizer.GetString("Farewell");
                Assert.AreEqual("Goodbye", english2.Value, "Second English request should return 'Goodbye'");

                // Step 4: Request in Norwegian - THIS IS WHERE THE BUG MANIFESTS
                // IsUiCultureCurrentCulture("nb-NO") returns TRUE because _currentCulture is "nb-NO"
                // No reload happens, but Localization is still pointing to en-US blob!
                CultureInfo.CurrentUICulture = new CultureInfo("nb-NO");
                var norwegian2 = localizer.GetString("Farewell");

                // Before fix: returns "Goodbye" (English) because Localization is en-US blob
                // After fix: returns "Ha det" (Norwegian)
                Assert.AreEqual("Ha det", norwegian2.Value,
                    "Second Norwegian request should return 'Ha det', not 'Goodbye'");
            }
            finally
            {
                CultureInfo.CurrentUICulture = originalCurrentUICulture;
            }
        }

        /// <summary>
        /// Tests the exact bug scenario with three cultures to ensure proper state management.
        /// </summary>
        [TestMethod]
        public void Should_Handle_Three_Culture_Round_Trip()
        {
            // Arrange
            var localizer = CreateLocalizer();
            var originalCurrentUICulture = CultureInfo.CurrentUICulture;

            try
            {
                // Cache all three cultures
                CultureInfo.CurrentUICulture = new CultureInfo("en-US");
                var en1 = localizer.GetString("Greeting");
                Assert.AreEqual("Hello", en1.Value);

                CultureInfo.CurrentUICulture = new CultureInfo("nb-NO");
                var nb1 = localizer.GetString("Greeting");
                Assert.AreEqual("Hei", nb1.Value);

                CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");
                var fr1 = localizer.GetString("Greeting");
                Assert.AreEqual("Bonjour", fr1.Value);

                // Now cycle back through - each switch to a cached culture could trigger the bug
                CultureInfo.CurrentUICulture = new CultureInfo("en-US");
                var en2 = localizer.GetString("Farewell");
                Assert.AreEqual("Goodbye", en2.Value, "Second English lookup should return 'Goodbye'");

                CultureInfo.CurrentUICulture = new CultureInfo("nb-NO");
                var nb2 = localizer.GetString("Farewell");
                Assert.AreEqual("Ha det", nb2.Value, "Second Norwegian lookup should return 'Ha det'");

                CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");
                var fr2 = localizer.GetString("Farewell");
                Assert.AreEqual("Au revoir", fr2.Value, "Second French lookup should return 'Au revoir'");

                // One more round to be thorough
                CultureInfo.CurrentUICulture = new CultureInfo("nb-NO");
                var nb3 = localizer.GetString("Welcome");
                Assert.AreEqual("Velkommen til applikasjonen var", nb3.Value,
                    "Third Norwegian lookup should return Norwegian welcome");
            }
            finally
            {
                CultureInfo.CurrentUICulture = originalCurrentUICulture;
            }
        }

        /// <summary>
        /// Tests rapid culture switching that would trigger the bug multiple times.
        /// </summary>
        [TestMethod]
        public void Should_Handle_Rapid_Culture_Switching()
        {
            // Arrange
            var localizer = CreateLocalizer();
            var originalCurrentUICulture = CultureInfo.CurrentUICulture;

            try
            {
                // Warm up all caches first
                CultureInfo.CurrentUICulture = new CultureInfo("en-US");
                localizer.GetString("Greeting");
                CultureInfo.CurrentUICulture = new CultureInfo("nb-NO");
                localizer.GetString("Greeting");
                CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");
                localizer.GetString("Greeting");
                CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
                localizer.GetString("Greeting");

                // Now rapidly switch between cultures - each switch is a cache HIT
                var results = new System.Collections.Generic.List<(string culture, string expected, string actual)>();

                void TestLookup(string culture, string expected)
                {
                    CultureInfo.CurrentUICulture = new CultureInfo(culture);
                    var result = localizer.GetString("Farewell");
                    results.Add((culture, expected, result.Value));
                }

                // Rapid switching - each one could trigger the bug
                TestLookup("nb-NO", "Ha det");
                TestLookup("en-US", "Goodbye");
                TestLookup("fr-FR", "Au revoir");
                TestLookup("de-DE", "Auf Wiedersehen");
                TestLookup("nb-NO", "Ha det");
                TestLookup("fr-FR", "Au revoir");
                TestLookup("en-US", "Goodbye");
                TestLookup("de-DE", "Auf Wiedersehen");
                TestLookup("fr-FR", "Au revoir");
                TestLookup("nb-NO", "Ha det");

                // Verify all results
                foreach (var (culture, expected, actual) in results)
                {
                    Assert.AreEqual(expected, actual,
                        $"Culture={culture}: Expected '{expected}' but got '{actual}'");
                }
            }
            finally
            {
                CultureInfo.CurrentUICulture = originalCurrentUICulture;
            }
        }

        /// <summary>
        /// Tests that the bug manifests when switching back to a previously-cached culture
        /// without any fallback involved - pure culture switch scenario.
        /// </summary>
        [TestMethod]
        public void Should_Correctly_Reload_Culture_After_Cache_Hit()
        {
            // Arrange
            var localizer = CreateLocalizer();
            var originalCurrentUICulture = CultureInfo.CurrentUICulture;

            try
            {
                // First: Load German (caches de-DE, _currentCulture = "de-DE")
                CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
                var german1 = localizer.GetString("Greeting");
                Assert.AreEqual("Hallo", german1.Value);

                // Second: Load Norwegian (caches nb-NO, _currentCulture = "nb-NO")
                CultureInfo.CurrentUICulture = new CultureInfo("nb-NO");
                var norwegian1 = localizer.GetString("Greeting");
                Assert.AreEqual("Hei", norwegian1.Value);

                // Third: Load German again (cache HIT for de-DE)
                // This is where the bug would occur:
                // - InitJsonStringLocalizer("de-DE") returns true (cache HIT)
                // - Localization is set to de-DE blob
                // - BUT _currentCulture is NOT updated, stays "nb-NO"
                CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
                var german2 = localizer.GetString("Error.Title.Validation");
                Assert.AreEqual("Validierungsfehler", german2.Value,
                    "Second German request should return 'Validierungsfehler'");

                // Fourth: Load Norwegian - THE BUG MANIFESTS HERE
                // - IsUiCultureCurrentCulture("nb-NO") returns TRUE (because _currentCulture is "nb-NO")
                // - No reload happens!
                // - But Localization is still de-DE blob from step 3!
                CultureInfo.CurrentUICulture = new CultureInfo("nb-NO");
                var norwegian2 = localizer.GetString("Error.Title.Validation");

                // Before fix: returns "Validierungsfehler" (German!)
                // After fix: returns "Valideringsfeil" (Norwegian)
                Assert.AreEqual("Valideringsfeil", norwegian2.Value,
                    "Second Norwegian request should return 'Valideringsfeil', not 'Validierungsfehler'");
            }
            finally
            {
                CultureInfo.CurrentUICulture = originalCurrentUICulture;
            }
        }

        /// <summary>
        /// Tests fallback behavior - when a key doesn't exist in current culture,
        /// subsequent lookups should still return the correct culture.
        /// </summary>
        [TestMethod]
        public void Should_Return_Correct_Culture_After_Fallback_Lookup()
        {
            // Arrange
            var localizer = CreateLocalizer();
            var originalCurrentUICulture = CultureInfo.CurrentUICulture;

            try
            {
                // First, "warm up" the English cache
                CultureInfo.CurrentUICulture = new CultureInfo("en-US");
                var englishGreeting = localizer.GetString("Greeting");
                Assert.AreEqual("Hello", englishGreeting.Value);

                // Now switch to Norwegian
                CultureInfo.CurrentUICulture = new CultureInfo("nb-NO");

                // Look up a key that ONLY exists in English - triggers fallback
                var fallbackResult = localizer.GetString("Error.EnglishOnly");
                Assert.AreEqual("This message only exists in English", fallbackResult.Value);

                // Now look up a key that exists in BOTH cultures
                var norwegianValidation = localizer.GetString("Error.Title.Validation");

                Assert.AreEqual("Valideringsfeil", norwegianValidation.Value,
                    "After fallback lookup, subsequent lookups should still use Norwegian");
            }
            finally
            {
                CultureInfo.CurrentUICulture = originalCurrentUICulture;
            }
        }

        /// <summary>
        /// Comprehensive test covering all scenarios that could trigger the bug.
        /// </summary>
        [TestMethod]
        public void Should_Maintain_Correct_Culture_State_Through_Complex_Sequence()
        {
            // Arrange
            var localizer = CreateLocalizer();
            var originalCurrentUICulture = CultureInfo.CurrentUICulture;

            try
            {
                var results = new System.Collections.Generic.List<(string culture, string key, string expected, string actual)>();

                void AssertLookup(string culture, string key, string expected)
                {
                    CultureInfo.CurrentUICulture = new CultureInfo(culture);
                    var result = localizer.GetString(key);
                    results.Add((culture, key, expected, result.Value));
                }

                // Complex sequence that exercises all code paths:
                // 1. Initial loads (cache misses)
                AssertLookup("en-US", "Greeting", "Hello");
                AssertLookup("nb-NO", "Greeting", "Hei");
                AssertLookup("fr-FR", "Greeting", "Bonjour");
                AssertLookup("de-DE", "Greeting", "Hallo");

                // 2. Switch back to cached cultures (cache HITs - this is where the bug occurs)
                AssertLookup("en-US", "Farewell", "Goodbye");
                AssertLookup("nb-NO", "Farewell", "Ha det");
                AssertLookup("fr-FR", "Farewell", "Au revoir");
                AssertLookup("de-DE", "Farewell", "Auf Wiedersehen");

                // 3. Fallback scenarios (keys only in English)
                AssertLookup("nb-NO", "Error.EnglishOnly", "This message only exists in English");
                AssertLookup("nb-NO", "Welcome", "Velkommen til applikasjonen var"); // Should still be Norwegian!

                AssertLookup("fr-FR", "Error.EnglishOnly", "This message only exists in English");
                AssertLookup("fr-FR", "Welcome", "Bienvenue dans notre application"); // Should still be French!

                // 4. More rapid switching
                AssertLookup("de-DE", "Error.Title.Validation", "Validierungsfehler");
                AssertLookup("nb-NO", "Error.Title.Validation", "Valideringsfeil");
                AssertLookup("en-US", "Error.Title.Validation", "Validation Error");
                AssertLookup("fr-FR", "Error.Title.Validation", "Erreur de validation");

                // Verify all results
                foreach (var (culture, key, expected, actual) in results)
                {
                    Assert.AreEqual(expected, actual,
                        $"Culture={culture}, Key={key}: Expected '{expected}' but got '{actual}'");
                }
            }
            finally
            {
                CultureInfo.CurrentUICulture = originalCurrentUICulture;
            }
        }

        #endregion

        #region Combined Tests

        /// <summary>
        /// Tests both fixes together in a realistic multi-request scenario.
        /// </summary>
        [TestMethod]
        public void Should_Handle_Concurrent_Culture_Switches_Correctly()
        {
            // Arrange
            var localizer = CreateLocalizer();
            var originalDefaultUICulture = CultureInfo.DefaultThreadCurrentUICulture;
            var originalCurrentUICulture = CultureInfo.CurrentUICulture;

            try
            {
                // Set process-wide default
                CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

                // Simulate rapid culture switches (like concurrent requests)
                var results = new System.Collections.Generic.Dictionary<string, string>();

                CultureInfo.CurrentUICulture = new CultureInfo("nb-NO");
                results["nb-NO-1"] = localizer.GetString("Greeting").Value;

                CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");
                results["fr-FR-1"] = localizer.GetString("Greeting").Value;

                CultureInfo.CurrentUICulture = new CultureInfo("nb-NO");
                results["nb-NO-2"] = localizer.GetString("Greeting").Value;

                CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
                results["de-DE-1"] = localizer.GetString("Greeting").Value;

                CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");
                results["fr-FR-2"] = localizer.GetString("Greeting").Value;

                // Assert all results are correct
                Assert.AreEqual("Hei", results["nb-NO-1"]);
                Assert.AreEqual("Bonjour", results["fr-FR-1"]);
                Assert.AreEqual("Hei", results["nb-NO-2"], "Second Norwegian request should still return Norwegian");
                Assert.AreEqual("Hallo", results["de-DE-1"]);
                Assert.AreEqual("Bonjour", results["fr-FR-2"], "Second French request should still return French");
            }
            finally
            {
                CultureInfo.DefaultThreadCurrentUICulture = originalDefaultUICulture;
                CultureInfo.CurrentUICulture = originalCurrentUICulture;
            }
        }

        #endregion
    }
}
