using TweakFirmware.Core;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Главное, что здесь проверяется: установка обновления не может начаться, пока идёт
    /// конвертирование, сборка или проверка. Руками этот случай почти не поймать — нужно
    /// успеть нажать кнопку в середине многогигабайтной операции, а последствия (битые
    /// файлы от принудительного закрытия) видны не сразу.
    /// </summary>
    public class UpdateFlowTests
    {
        private static UpdateFlow Downloaded(string version = "2.6.0")
        {
            var flow = new UpdateFlow();
            flow.OnUpdateFound(version);
            Assert.True(flow.TryBeginDownload());
            flow.OnDownloadCompleted(@"C:\Temp\TweakFirmwareSetup.exe");
            return flow;
        }

        // ============ Главный инвариант ============

        [Fact]
        public void Install_Blocked_WhileOperationRunning()
        {
            var flow = Downloaded();
            flow.SetOperationRunning(true);

            Assert.False(flow.CanInstall);
            Assert.False(flow.TryBeginInstall());
            Assert.Equal(UpdateStage.ReadyToInstall, flow.Stage);
        }

        [Fact]
        public void Install_Allowed_AfterOperationFinishes()
        {
            var flow = Downloaded();
            flow.SetOperationRunning(true);
            Assert.False(flow.TryBeginInstall());

            flow.SetOperationRunning(false);

            Assert.True(flow.CanInstall);
            Assert.True(flow.TryBeginInstall());
            Assert.Equal(UpdateStage.Installing, flow.Stage);
        }

        [Theory]
        [InlineData(UpdateStage.None)]
        [InlineData(UpdateStage.Available)]
        [InlineData(UpdateStage.Downloading)]
        [InlineData(UpdateStage.ReadyToInstall)]
        public void TryBeginInstall_FalseWhenOperationRunning_InEveryStage(UpdateStage stage)
        {
            // Защиту нельзя обойти, зайдя в установку из другого состояния.
            var flow = new UpdateFlow();
            switch (stage)
            {
                case UpdateStage.Available:
                    flow.OnUpdateFound("2.6.0");
                    break;
                case UpdateStage.Downloading:
                    flow.OnUpdateFound("2.6.0");
                    flow.TryBeginDownload();
                    break;
                case UpdateStage.ReadyToInstall:
                    flow = Downloaded();
                    break;
            }
            flow.SetOperationRunning(true);

            Assert.Equal(stage, flow.Stage);
            Assert.False(flow.TryBeginInstall());
            Assert.Equal(stage, flow.Stage);
        }

        [Fact]
        public void OperationStartedMidDownload_InstallStillBlockedWhenDownloadCompletes()
        {
            // Гонка: скачивание началось на простое, а операция запустилась во время него.
            var flow = new UpdateFlow();
            flow.OnUpdateFound("2.6.0");
            Assert.True(flow.TryBeginDownload());

            flow.SetOperationRunning(true);
            flow.OnDownloadCompleted(@"C:\Temp\setup.exe");

            Assert.Equal(UpdateStage.ReadyToInstall, flow.Stage);
            Assert.True(flow.IsWaitingForOperation);
            Assert.False(flow.TryBeginInstall());
        }

        // ============ Скачивание не запускает установку само ============

        [Fact]
        public void DownloadCompleted_DoesNotAutoInstall()
        {
            var flow = Downloaded();

            Assert.Equal(UpdateStage.ReadyToInstall, flow.Stage);
            Assert.NotEqual(UpdateStage.Installing, flow.Stage);
        }

        [Fact]
        public void Download_AllowedWhileOperationRunning()
        {
            // Скачивать во время операции можно — нельзя только ставить.
            var flow = new UpdateFlow();
            flow.OnUpdateFound("2.6.0");
            flow.SetOperationRunning(true);

            Assert.True(flow.CanDownload);
            Assert.True(flow.TryBeginDownload());
        }

        [Fact]
        public void TryBeginInstall_Twice_SecondCallRefused()
        {
            var flow = Downloaded();

            Assert.True(flow.TryBeginInstall());
            Assert.False(flow.TryBeginInstall());
        }

        [Fact]
        public void TryBeginInstall_BeforeDownload_Refused()
        {
            var flow = new UpdateFlow();
            flow.OnUpdateFound("2.6.0");

            Assert.False(flow.CanInstall);
            Assert.False(flow.TryBeginInstall());
        }

        [Fact]
        public void InstallFailed_ReturnsToReadyAndCanRetry()
        {
            var flow = Downloaded();
            Assert.True(flow.TryBeginInstall());

            flow.OnInstallFailed();

            Assert.Equal(UpdateStage.ReadyToInstall, flow.Stage);
            Assert.True(flow.TryBeginInstall());
        }

        // ============ Баннер и «Позже» ============

        [Fact]
        public void Postpone_HidesBanner_ButKeepsInstallerReady()
        {
            var flow = Downloaded();

            flow.Postpone();

            Assert.False(flow.IsBannerVisible);
            Assert.Equal(UpdateStage.ReadyToInstall, flow.Stage);
            Assert.NotNull(flow.InstallerPath);
            Assert.True(flow.CanInstall);
        }

        [Fact]
        public void RestoreDownloaded_AfterRestart_OffersInstallWithoutDownloadingAgain()
        {
            var flow = new UpdateFlow();

            flow.RestoreDownloaded("2.6.0", @"C:\Temp\setup.exe");

            Assert.Equal(UpdateStage.ReadyToInstall, flow.Stage);
            Assert.True(flow.IsBannerVisible);
            Assert.True(flow.CanInstall);
            Assert.False(flow.CanDownload);
        }

        [Fact]
        public void RestoreDownloaded_IgnoresEmptyValues()
        {
            var flow = new UpdateFlow();

            flow.RestoreDownloaded("", @"C:\Temp\setup.exe");
            flow.RestoreDownloaded("2.6.0", "");

            Assert.Equal(UpdateStage.None, flow.Stage);
            Assert.False(flow.IsBannerVisible);
        }

        [Fact]
        public void OnUpdateFound_SameVersionAlreadyDownloaded_KeepsReadyToInstall()
        {
            // Ежедневная проверка не должна сбрасывать уже скачанный установщик.
            var flow = Downloaded("2.6.0");
            string? path = flow.InstallerPath;

            flow.OnUpdateFound("2.6.0");

            Assert.Equal(UpdateStage.ReadyToInstall, flow.Stage);
            Assert.Equal(path, flow.InstallerPath);
        }

        [Fact]
        public void OnUpdateFound_NewerVersion_RequiresDownloadAgain()
        {
            var flow = Downloaded("2.6.0");

            flow.OnUpdateFound("2.7.0");

            Assert.Equal(UpdateStage.Available, flow.Stage);
            Assert.Null(flow.InstallerPath);
            Assert.Equal("2.7.0", flow.LatestVersion);
        }

        [Fact]
        public void OnUpdateFound_WhileInstalling_Ignored()
        {
            var flow = Downloaded();
            Assert.True(flow.TryBeginInstall());

            flow.OnUpdateFound("2.7.0");

            Assert.Equal(UpdateStage.Installing, flow.Stage);
        }

        [Fact]
        public void DownloadInterrupted_ReturnsToAvailable()
        {
            var flow = new UpdateFlow();
            flow.OnUpdateFound("2.6.0");
            Assert.True(flow.TryBeginDownload());

            flow.OnDownloadInterrupted();

            Assert.Equal(UpdateStage.Available, flow.Stage);
            Assert.Null(flow.InstallerPath);
            Assert.True(flow.CanDownload);
        }

        [Fact]
        public void NoUpdate_BannerHidden()
        {
            var flow = new UpdateFlow();

            Assert.Equal(UpdateStage.None, flow.Stage);
            Assert.False(flow.IsBannerVisible);
            Assert.False(flow.CanDownload);
            Assert.False(flow.CanInstall);
        }

        [Fact]
        public void Changed_RaisedOnOperationStateSwitch_ButNotOnRepeatedSameValue()
        {
            // Баннер и кнопки обновляются по этому событию; лишние срабатывания дают
            // холостую перерисовку на каждый отчёт о прогрессе операции.
            var flow = Downloaded();
            int count = 0;
            flow.Changed += () => count++;

            flow.SetOperationRunning(true);
            flow.SetOperationRunning(true);
            flow.SetOperationRunning(false);

            Assert.Equal(2, count);
        }
    }
}
