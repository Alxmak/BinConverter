using System;
using System.IO;

namespace TweakFirmware.Core.Operations
{
    /// <summary>Что удалось узнать о цепочке частей, не читая её содержимое.</summary>
    public sealed class ChainProbeResult
    {
        /// <summary>Цепочка найдена и все её файлы читаемы.</summary>
        public bool Resolved { get; init; }

        public int PartCount { get; init; }
        public long TotalBytes { get; init; }

        /// <summary>Имя базового файла цепочки — его показывает карточка «Общая информация».</summary>
        public string BaseFileName { get; init; } = "";

        /// <summary>Имя, которое стоит предложить для собранного файла.</summary>
        public string SuggestedOutputFileName { get; init; } = "";

        /// <summary>Почему не получилось. Пусто, если <see cref="Resolved"/>.</summary>
        public string ErrorMessage { get; init; } = "";

        /// <summary>
        /// Номер части, размер которой выбивается из остальных, — см. <see cref="ChainConsistency"/>.
        /// null, если цепочка ровная. Это не ошибка, а повод предупредить: собрать такую
        /// цепочку можно, но результат стоит сверить по хэшу.
        /// </summary>
        public int? UnevenPartNumber { get; init; }
    }

    /// <summary>
    /// Осмотр цепочки для предпросмотра: сколько частей, какой получится размер, как
    /// назвать результат. Отдельно от <see cref="MergeOperation"/>, потому что вызывается
    /// не перед работой, а по ходу ввода пути — и потому не имеет права ни бросать
    /// исключения, ни идти в потоке интерфейса.
    ///
    /// Раньше это делала MergeViewModel: на каждое нажатие клавиши в поле пути она
    /// перебирала цепочку на диске и ловила исключения сама.
    /// </summary>
    public static class ChainProbe
    {
        public static ChainProbeResult Measure(string? anyChainFilePath)
        {
            if (string.IsNullOrWhiteSpace(anyChainFilePath))
                return new ChainProbeResult();

            try
            {
                long total = MergeOperation.GetChainSize(anyChainFilePath, out var chain);
                string basePath = HashHelper.ResolveBasePath(anyChainFilePath);

                return new ChainProbeResult
                {
                    Resolved = true,
                    PartCount = chain.Count,
                    TotalBytes = total,
                    BaseFileName = Path.GetFileName(chain[0]),
                    SuggestedOutputFileName = MergeOutputNaming.SuggestFileName(basePath),
                    UnevenPartNumber = ChainConsistency.FindUnevenPartNumber(chain)
                };
            }
            catch (Exception ex)
            {
                return new ChainProbeResult { ErrorMessage = ex.Message };
            }
        }
    }
}
