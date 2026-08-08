using CommunityToolkit.Mvvm.ComponentModel;
using TweakFirmware.Services;

namespace TweakFirmware.ViewModels
{
    /// <summary>
    /// ViewModel, часть текстов которой задаётся кодом, а не разметкой.
    ///
    /// Разметка обновляется при смене языка сама: {loc:Loc Key} — это привязка к
    /// индексатору <see cref="LocalizationService"/>. А строка, присвоенная свойству
    /// один раз (в конструкторе, в инициализаторе поля или по ходу работы), остаётся
    /// на том языке, который был в тот момент. Из-за этого после переключения языка
    /// на вкладках оставались русские подписи: «Пользовательский» в списке моделей,
    /// «Файл 1»/«Файл 2», «Сравнение ещё не выполнялось», содержимое «Общей информации».
    ///
    /// <see cref="Attach"/> вызывается при появлении страницы на экране,
    /// <see cref="Detach"/> — при уходе с неё. Именно парой, а не только подпиской
    /// в конструкторе: страница может создаваться заново при каждом переходе, и тогда
    /// обработчики копились бы в статическом событии до конца работы программы.
    /// </summary>
    public abstract class LocalizedViewModel : ObservableObject
    {
        private bool _subscribed;

        /// <summary>Страница показана — начинаем следить за языком.</summary>
        public void Attach()
        {
            if (_subscribed) return;

            LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
            _subscribed = true;
        }

        /// <summary>Страница скрыта — отписываемся, чтобы обработчик не остался висеть.</summary>
        public void Detach()
        {
            if (!_subscribed) return;

            LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
            _subscribed = false;
        }

        /// <summary>
        /// Пересобрать тексты, заданные кодом. Вызывается уже после того, как
        /// <see cref="Strings"/> переключён, поэтому достаточно перечитать нужные ключи.
        /// </summary>
        protected virtual void OnLanguageChanged() { }
    }
}
