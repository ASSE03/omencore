using System.Globalization;
using FluentAssertions;
using OmenCore.Localization;
using OmenCore.Services.BloatwareManager;
using Xunit;

namespace OmenCoreApp.Tests.Localization
{
    /// <summary>
    /// CoreText translates English that OmenCore.Core emits, at the UI boundary. In English it must
    /// hand the text back untouched; in French it must translate templates, their captured values
    /// and scope lists, and leave anything it does not know alone.
    /// </summary>
    public class CoreTextTests
    {
        private static T InFrench<T>(System.Func<T> body)
        {
            var original = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr");
                return body();
            }
            finally
            {
                CultureInfo.CurrentUICulture = original;
            }
        }

        [Fact]
        public void English_IsReturnedUnchanged()
        {
            CoreText.Translate("Scheduled task disabled successfully.").Should().Be("Scheduled task disabled successfully.");
            CoreText.Translate("Package is still present after removal attempt (all users, provisioned image).")
                .Should().Be("Package is still present after removal attempt (all users, provisioned image).");
        }

        [Fact]
        public void French_TranslatesExactText()
        {
            InFrench(() => CoreText.Translate("Scheduled task disabled successfully.")).Should().Be("Tâche planifiée désactivée.");
            InFrench(() => CoreText.Translate("HP scheduled task")).Should().Be("Tâche planifiée HP");
        }

        [Fact]
        public void French_TranslatesTemplateAndScopeList()
        {
            InFrench(() => CoreText.Translate("Package is still present after removal attempt (all users, provisioned image)."))
                .Should().Be("Le paquet est toujours présent après la tentative de suppression (tous les utilisateurs, image préinstallée).");
        }

        [Fact]
        public void UnknownText_AndAppNames_AreLeftAlone()
        {
            InFrench(() => CoreText.Translate("Some message Core added later")).Should().Be("Some message Core added later");
            InFrench(() => CoreText.Translate("Removing Mixed Reality Portal...")).Should().Be("Suppression de Mixed Reality Portal...");
        }

        [Fact]
        public void EnumNames_UseTheCatalog_AndFallBackToTheRawName()
        {
            InFrench(() => CoreText.EnumName(RemovalRisk.Low)).Should().Be("Faible");
            InFrench(() => CoreText.EnumName(BloatwareType.ScheduledTask)).Should().Be("Tâche planifiée");
            CoreText.EnumName(RemovalRisk.High).Should().Be("High");
        }
    }
}
