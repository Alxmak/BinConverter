using CommunityToolkit.Mvvm.ComponentModel;
using TweakFirmware.Core;

namespace TweakFirmware.Models
{
    /// <summary>
    /// Строка списка единиц рядом с полем лимита: байты, КБ, МБ, ГБ.
    ///
    /// Название — свойство с уведомлением, как у <see cref="ProgrammerPreset"/>:
    /// при смене языка ConvertViewModel обновляет его на месте. Заменять сам элемент
    /// в списке нельзя — на него указывает SelectedLimitUnit, и подмена сбросила бы
    /// выбор пользователя на первый пункт.
    /// </summary>
    public partial class SizeUnitOption : ObservableObject
    {
        [ObservableProperty] private string name;

        public SizeUnit Unit { get; }

        public SizeUnitOption(SizeUnit unit, string name)
        {
            Unit = unit;
            this.name = name;
        }

        public override string ToString() => Name;
    }
}
