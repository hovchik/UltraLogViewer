import { ulv } from './api';
import { h, modal } from './util';

/** Help › About: name, version, what the app is, copyright and build date (all from package.json and the build). */
export async function showAbout() {
  const info = await ulv().appInfo();
  modal((box, close) => {
    box.classList.add('about');
    const meta = [info.buildDate && `Built ${info.buildDate}`, info.electron && `Electron ${info.electron}`].filter(Boolean) as string[];
    box.append(
      h('div', { class: 'top' }, h('img', { src: 'app.png', alt: '' }), h('div', {}, h('h2', {}, info.name), h('div', { class: 'ver' }, `Version ${info.version}`))),
      h('p', { class: 'desc' }, info.description),
      h('div', { class: 'copy' }, info.copyright),
      ...meta.map((m) => h('div', { class: 'meta' }, m)),
      h(
        'div',
        { class: 'actions' },
        h('button', { class: 'btn', onclick: () => void ulv().openHomepage() }, 'Project on GitHub'),
        h('button', { class: 'btn primary', onclick: close }, 'Close'),
      ),
    );
  });
  // (after modal(), which focuses the first button: Enter should close, not open the browser)
  (document.querySelector('.modal .about .btn.primary') as HTMLElement | null)?.focus();
}
