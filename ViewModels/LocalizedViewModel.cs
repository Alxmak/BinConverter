using CommunityToolkit.Mvvm.ComponentModel;
using TweakFirmware.Core.Localization;
using TweakFirmware.Services;

namespace TweakFirmware.ViewModels
{
    /// <summary>
    /// ViewModel, часть текстов которой задаётся кодом, а не разметкой.
    ///
    /// Разметка обновляется при смене языка сама: {loc:Loc Key} — это привязка к
    /// индексатору <see cref="LocalizationService"/>. А строка, присвоенная свойству
    /// один раз (в конструкторе, в инициализаторе поля или по ходу работы), остаётся
    /// на том языке, который был в тот момент: «Пользовательский» в списке моделей,
    /// «Файл 1»/«Файл 2», «Сравнение ещё не выполнялось», содержимое «Общей информации».
    ///
    /// Обновление устроено двумя путями, и оба нужны:
    ///
    /// 1. Сверка языка в <see cref="Attach"/>. Это основной путь. Язык переключают
    ///    в «Настройках», то есть в момент переключения ни одна другая вкладка не видна,
    ///    а её ViewModel (она живёт дольше страницы, см. <see cref="TabViewModels"/>)
    ///    в это время отписана. Поэтому при возвращении на вкладку мы сравниваем язык
    ///    с тем, при котором собирали тексты в прошлый раз, и пересобираем, если разошлось.
    ///
    /// 2. Подписка на событие — на случай, когда страница видна в момент переключения.
    ///    Сейчас так может быть только у самих «Настроек», но переключатель языка вполне
    ///    может появиться и в другом месте, и тогда это сработает без доработок.
    /// </summary>
    public abstract class LocalizedViewModel : ObservableObject
    {
        private bool _subscribed;

        /// <summary>Язык, на котором собраны текущие тексты этой ViewModel.</summary>
        private string _textsBuiltFor = Strings.CurrentLanguage;

        /// <summary>Страница показана: дособираем тексты, если язык сменился без нас.</summary>
        public void Attach()
        {
            if (!_subscribed)
            {
                LocalizationService.Instance.LanguageChanged += HandleLanguageChanged;
                _subscribed = true;
            }

            if (_textsBuiltFor != Strings.CurrentLanguage) HandleLanguageChanged();

            OnAttached();
        }

        /// <summary>Страница скрыта — отписываемся, чтобы обработчик не остался висеть.</summary>
        public void Detach()
        {
            if (!_subscribed) return;

            LocalizationService.Instance.LanguageChanged -= HandleLanguageChanged;
            _subscribed = false;
        }

        private void HandleLanguageChanged()
        {
            _textsBuiltFor = Strings.CurrentLanguage;
            OnLanguageChanged();
        }

        /// <summary>
        /// Пересобрать тексты, заданные кодом. Вызывается уже после того, как
        /// <see cref="Strings"/> переключён, поэтому достаточно перечитать нужные ключи.
        /// </summary>
        protected virtual void OnLanguageChanged() { }

        /// <summary>
        /// Вкладку открыли. Здесь стоит обновлять то, что могли поменять в других
        /// разделах, — например папку по умолчанию из «Настроек»: раньше она перечитывалась
        /// сама собой, потому что ViewModel создавалась заново.
        /// </summary>
        protected virtual void OnAttached() { }
    }
}
