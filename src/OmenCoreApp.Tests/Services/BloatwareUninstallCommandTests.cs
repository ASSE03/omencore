using System;
using FluentAssertions;
using OmenCore.Services.BloatwareManager;
using Xunit;

namespace OmenCoreApp.Tests.Services
{
    public class BloatwareUninstallCommandTests
    {
        [Fact]
        public void MsiInstallSwitch_BecomesSilentUninstall()
        {
            var (file, args) = BloatwareManagerService.ParseWin32UninstallCommand("MsiExec.exe /I{11111111-2222-3333-4444-555555555555}");
            file.Should().Be("msiexec.exe");
            args.Should().Contain("/X{11111111-2222-3333-4444-555555555555}").And.Contain("/qn");
        }

        [Fact]
        public void QuotedExe_GetsGenericSilentSwitches_WhenNotVendorQuiet()
        {
            var (file, args) = BloatwareManagerService.ParseWin32UninstallCommand(@"""C:\Program Files\HP\uninst.exe""");
            file.Should().Be(@"C:\Program Files\HP\uninst.exe");
            args.Should().Contain("/S");
        }

        [Fact]
        public void VendorQuietCommand_IsUsedVerbatim()
        {
            var (file, args) = BloatwareManagerService.ParseWin32UninstallCommand(
                @"""C:\Program Files\HP\uninst.exe"" --mode unattended", isQuietCommand: true);
            file.Should().Be(@"C:\Program Files\HP\uninst.exe");
            args.Should().Be("--mode unattended");
        }

        [Fact]
        public void UnquotedPathWithSpaces_SplitsAtExe()
        {
            var (file, args) = BloatwareManagerService.ParseWin32UninstallCommand(@"C:\Program Files\HP\uninst.exe -remove", isQuietCommand: true);
            file.Should().Be(@"C:\Program Files\HP\uninst.exe");
            args.Should().Be("-remove");
        }

        // ─── AppX removal script ────────────────────────────────────────────

        [Fact]
        public void AppxRemoval_RunsEveryScope_NotJustTheFirstThatSucceeds()
        {
            var script = BloatwareManagerService.BuildAppxRemovalScript("Microsoft.ZuneVideo");

            script.Should().Contain("Get-AppxPackage -AllUsers -Name $n | Remove-AppxPackage -AllUsers");
            script.Should().Contain("Get-AppxPackage -Name $n | Remove-AppxPackage");
            script.Should().Contain("Remove-AppxProvisionedPackage -Online");

            // The old script exited after the first scope that did not throw, leaving the package
            // installed for other users and in the provisioned image. Only one exit decision, at the end.
            script.IndexOf("exit 0", StringComparison.Ordinal)
                .Should().BeGreaterThan(script.IndexOf("Remove-AppxProvisionedPackage", StringComparison.Ordinal));
        }

        [Fact]
        public void AppxRemoval_UsesTheEscapedName()
        {
            BloatwareManagerService.BuildAppxRemovalScript("O''Brien.App").Should().StartWith("$n='O''Brien.App';");
        }
    }
}
