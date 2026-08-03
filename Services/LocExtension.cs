using System;
using System.Windows.Data;
using System.Windows.Markup;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Короткая запись в XAML: {loc:Loc SomeKey} вместо полной привязки к индексатору
    /// LocalizationService. Под капотом — обычный Binding к индексатору, поэтому
    /// живо обновляется при переключении языка (см. LocalizationService.Apply).
    /// </summary>
    public class LocExtension : MarkupExtension
    {
        [ConstructorArgument("key")]
        public string Key { get; set; }

        public LocExtension() : this(string.Empty) { }

        public LocExtension(string key)
        {
            Key = key;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var binding = new Binding($"[{Key}]")
            {
                Source = LocalizationService.Instance,
                Mode = BindingMode.OneWay
            };
            return binding.ProvideValue(serviceProvider);
        }
    }
}
