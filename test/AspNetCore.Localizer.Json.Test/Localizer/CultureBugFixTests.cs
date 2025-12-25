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
