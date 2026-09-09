AB.pages = AB.pages || {};
AB.pages.help = {
  _lang: (typeof localStorage !== 'undefined' && localStorage.getItem('ab_help_lang')) || 'ru',
  render() {
    this._applyLang(this._lang);
  },
  _applyLang(lang) {
    this._lang = lang === 'en' ? 'en' : 'ru';
    try { localStorage.setItem('ab_help_lang', this._lang); } catch (_) {}
    document.querySelectorAll('.help-lang-btn').forEach(b => {
      b.classList.toggle('active', b.getAttribute('data-lang') === this._lang);
    });
    document.querySelectorAll('.help-lang-pane').forEach(p => {
      p.classList.toggle('active', p.getAttribute('data-help-lang') === this._lang);
    });
  },
  onShow() { this.render(); }
};

document.querySelectorAll('.help-lang-btn').forEach(btn => {
  btn.addEventListener('click', () => {
    AB.pages.help._applyLang(btn.getAttribute('data-lang'));
  });
});
