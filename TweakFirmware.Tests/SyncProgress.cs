using System;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Синхронный <see cref="IProgress{T}"/> для тестов.
    ///
    /// System.Progress&lt;T&gt; доставляет колбэк через SynchronizationContext или пул потоков,
    /// то есть асинхронно. В тестах это приводило к настоящему плавающему падению: отмена,
    /// запрошенная из колбэка прогресса, успевала сработать уже после конца работы, и тест
    /// «отмена посередине» иногда видел завершённую операцию. Здесь Report() выполняется
    /// строго в момент вызова, поэтому поведение воспроизводимо.
    /// </summary>
    internal sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;

        public SyncProgress(Action<T> callback) => _callback = callback;

        public void Report(T value) => _callback(value);
    }
}
