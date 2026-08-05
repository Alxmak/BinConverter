using System;

namespace TweakFirmware.Core
{
    /// <summary>Стадия, на которой находится обновление.</summary>
    public enum UpdateStage
    {
        /// <summary>Обновлений нет либо ещё не проверяли.</summary>
        None,
        /// <summary>Новая версия найдена, установщик ещё не скачан.</summary>
        Available,
        /// <summary>Установщик скачивается.</summary>
        Downloading,
        /// <summary>Установщик скачан и ждёт разрешения на установку.</summary>
        ReadyToInstall,
        /// <summary>Установка запущена, приложение вот-вот закроется.</summary>
        Installing
    }

    /// <summary>
    /// Правила обновления, отделённые от их исполнения: здесь нет ни WPF, ни обращений
    /// к диску, ни запуска процессов — только решение "можно ли сейчас". Благодаря этому
    /// главный инвариант («установка не начинается, пока идёт конвертирование, сборка или
    /// проверка») покрывается обычными юнит-тестами: руками поймать нужный момент внутри
    /// многогигабайтной операции почти невозможно.
    ///
    /// Всё, что делает эффект (скачивание, запуск установщика, закрытие приложения),
    /// живёт в TweakFirmware.Services.UpdateManager и обязано спрашивать разрешение
    /// через <see cref="TryBeginDownload"/> и <see cref="TryBeginInstall"/>.
    /// </summary>
    public sealed class UpdateFlow
    {
        /// <summary>Срабатывает при любом изменении состояния — чтобы обновить баннер и кнопки.</summary>
        public event Action? Changed;

        public UpdateStage Stage { get; private set; } = UpdateStage.None;

        /// <summary>Идёт ли прямо сейчас конвертирование / сборка / проверка.</summary>
        public bool IsOperationRunning { get; private set; }

        public string LatestVersion { get; private set; } = "";

        /// <summary>Путь к скачанному установщику; null, пока он не скачан.</summary>
        public string? InstallerPath { get; private set; }

        /// <summary>Пользователь нажал «Позже» — баннер скрыт до следующего запуска.</summary>
        public bool IsPostponed { get; private set; }

        public bool IsBannerVisible => Stage != UpdateStage.None && !IsPostponed;

        public bool CanDownload => Stage == UpdateStage.Available;

        /// <summary>
        /// Установка разрешена только когда установщик скачан И ничего не выполняется.
        /// Единственное условие, от которого зависит <see cref="TryBeginInstall"/>.
        /// </summary>
        public bool CanInstall => Stage == UpdateStage.ReadyToInstall && !IsOperationRunning;

        /// <summary>Скачано, но установка недоступна именно из-за идущей операции — для подсказки в баннере.</summary>
        public bool IsWaitingForOperation => Stage == UpdateStage.ReadyToInstall && IsOperationRunning;

        public void OnUpdateFound(string version)
        {
            // Если установщик этой же версии уже скачан, повторная проверка не должна
            // отбрасывать нас назад к «надо скачать».
            if (Stage == UpdateStage.ReadyToInstall && LatestVersion == version) return;
            if (Stage == UpdateStage.Downloading || Stage == UpdateStage.Installing) return;

            LatestVersion = version;
            InstallerPath = null;
            IsPostponed = false;
            Stage = UpdateStage.Available;
            Notify();
        }

        /// <summary>Восстановление после перезапуска: установщик остался с прошлого раза.</summary>
        public void RestoreDownloaded(string version, string installerPath)
        {
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(installerPath)) return;
            if (Stage != UpdateStage.None) return;

            LatestVersion = version;
            InstallerPath = installerPath;
            IsPostponed = false;
            Stage = UpdateStage.ReadyToInstall;
            Notify();
        }

        public bool TryBeginDownload()
        {
            if (!CanDownload) return false;
            Stage = UpdateStage.Downloading;
            Notify();
            return true;
        }

        public void OnDownloadCompleted(string installerPath)
        {
            if (Stage != UpdateStage.Downloading) return;

            InstallerPath = installerPath;
            Stage = UpdateStage.ReadyToInstall;
            Notify();
        }

        /// <summary>Скачивание сорвалось или отменено — возвращаемся к «доступно обновление».</summary>
        public void OnDownloadInterrupted()
        {
            if (Stage != UpdateStage.Downloading) return;

            InstallerPath = null;
            Stage = UpdateStage.Available;
            Notify();
        }

        /// <summary>
        /// Единственная точка, разрешающая запуск установщика. Возвращает false, если идёт
        /// операция, если установщик не скачан или если установка уже была запущена.
        /// </summary>
        public bool TryBeginInstall()
        {
            if (!CanInstall) return false;
            Stage = UpdateStage.Installing;
            Notify();
            return true;
        }

        /// <summary>Установщик не запустился — можно попробовать снова.</summary>
        public void OnInstallFailed()
        {
            if (Stage != UpdateStage.Installing) return;
            Stage = UpdateStage.ReadyToInstall;
            Notify();
        }

        public void SetOperationRunning(bool running)
        {
            if (IsOperationRunning == running) return;
            IsOperationRunning = running;
            Notify();
        }

        /// <summary>«Позже»: баннер скрывается, но скачанный установщик сохраняется.</summary>
        public void Postpone()
        {
            if (IsPostponed) return;
            IsPostponed = true;
            Notify();
        }

        private void Notify() => Changed?.Invoke();
    }
}
