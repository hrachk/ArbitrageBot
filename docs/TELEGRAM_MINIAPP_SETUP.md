# ArbitrageBot · Telegram Mini App (Reports)

Мобильная панель отчётов внутри Telegram: Net PnL, Win Rate, PF, heatmap, Gross/Fees.

**URL внутри бота:** `https://ВАШ_ХОСТ/tg/`  
**API:** `GET /api/tg/report?days=7` (1 / 7 / 30 / 90 / 0=ALL)

---

## Что нужно

1. Запущенный **ArbitrageBot** (как обычно, порт 5050 или ваш).
2. **Публичный HTTPS** URL до этого хоста (Telegram **не** открывает `http://localhost`).
3. Бот в Telegram через **@BotFather**.

---

## Шаг 1 · Запуск бота ArbitrageBot

```bash
cd путь/к/ArbitrageBot
dotnet run --project ArbitrageBot
# или Publish + ArbitrageBot.exe
```

Проверка в браузере на ПК:

- `http://127.0.0.1:5050/` — основной UI  
- `http://127.0.0.1:5050/tg/` — Mini App  
- `http://127.0.0.1:5050/api/tg/report?days=7` — JSON отчёта  

---

## Шаг 2 · Публичный HTTPS (обязательно для Telegram)

### Вариант A · Cloudflare Tunnel (удобно, бесплатно)

1. Установите [cloudflared](https://developers.cloudflare.com/cloudflare-one/connections/connect-apps/install-and-setup/installation/).
2. В терминале:

```bash
cloudflared tunnel --url http://127.0.0.1:5050
```

3. В выводе будет URL вида:

```text
https://random-words.trycloudflare.com
```

4. Откройте в браузере:

```text
https://random-words.trycloudflare.com/tg/
```

Должна открыться тёмная панель отчётов.

> URL меняется при каждом новом запуске free tunnel. Для постоянного URL — именованный tunnel в Cloudflare Zero Trust.

### Вариант B · ngrok

```bash
ngrok http 5050
```

Скопируйте `https://….ngrok-free.app` → проверьте `/tg/`.

### Вариант C · VPS

Разверните бот на сервере с Nginx + Let's Encrypt, например:

```text
https://arb.yourdomain.com/tg/
```

---

## Шаг 3 · Создать Telegram-бота

1. Откройте Telegram → найдите **@BotFather**.
2. Команда `/newbot` → имя (например `Arbitrage Desk`) → username (например `my_arb_desk_bot`).
3. Сохраните **token** вида `123456:ABC-DEF...` (для меню не обязателен, но пригодится).

---

## Шаг 4 · Привязать Mini App к боту

### 4.1 Кнопка меню (рекомендуется)

В @BotFather:

```text
/mybots → выберите бота → Bot Settings → Menu Button → Configure menu button
```

- **Title:** `Reports` (или `Отчёты`)
- **URL:** `https://ВАШ_HTTPS_ХОСТ/tg/`

Пример:

```text
https://random-words.trycloudflare.com/tg/
```

### 4.2 Или команда с Web App (альтернатива)

@BotFather → `/setmenubutton` (если доступно)  
или создайте сообщение с кнопкой через Bot API (curl):

```bash
curl -s -X POST "https://api.telegram.org/bot<TOKEN>/setChatMenuButton" \
  -H "Content-Type: application/json" \
  -d '{
    "menu_button": {
      "type": "web_app",
      "text": "Reports",
      "web_app": { "url": "https://ВАШ_HTTPS_ХОСТ/tg/" }
    }
  }'
```

---

## Шаг 5 · Открыть на телефоне / планшете

1. Откройте чат **с вашим ботом** в Telegram (мобильный или desktop).
2. Нажмите кнопку **меню** слева от поля ввода (или `/start`, если настроили).
3. Откроется **Reports** Mini App на весь экран.
4. Переключайте **1D / 7D / 30D / 90D / ALL**, кнопка **↻** — обновить.
5. Авто-обновление каждые ~45 секунд.

**Планшет:** то же в Telegram для iPad / Android tablet.  
**Web Telegram:** https://web.telegram.org → тот же бот → Menu → Reports.

---

## Шаг 6 · Проверка, если пусто / ошибка

| Симптом | Что проверить |
|--------|----------------|
| «Ошибка загрузки» | HTTPS tunnel жив? Бот ArbitrageBot запущен? |
| Пустые KPI | Есть ли сделки в `data/paper/`? Сначала откройте `/api/tg/report` в браузере |
| Telegram: «link invalid» | URL только **https**, путь `/tg/` со слэшем |
| Старые данные | tunnel указывает на другой порт / старый процесс |

Локально без Telegram:

```text
http://127.0.0.1:5050/tg/
```

---

## Безопасность (важно)

Сейчас Mini App читает **те же** открытые API, что и веб-UI (`/api/tg/report`, analytics).

Если выставляете в интернет:

1. Не светите **trade** API keys в Settings на том же хосте без защиты.  
2. Желательно firewall / Basic Auth / VPN / только Cloudflare Access.  
3. Для личного использования достаточно **временного** tunnel + не публиковать URL.

(Опционально позже: проверка `Telegram.WebApp.initData` + shared secret.)

---

## Файлы в проекте

```text
ArbitrageBot/wwwroot/tg/index.html   — разметка
ArbitrageBot/wwwroot/tg/styles.css   — тёмный mobile UI
ArbitrageBot/wwwroot/tg/app.js       — загрузка /api/tg/report
GET /api/tg/report?days=7            — агрегат performance + summary + opens
```

---

## Краткий чеклист

- [ ] `dotnet run` — бот слушает 5050  
- [ ] Браузер: `/tg/` и `/api/tg/report?days=7` ок  
- [ ] `cloudflared` / ngrok / VPS → HTTPS  
- [ ] BotFather → Menu Button → `https://…/tg/`  
- [ ] Telegram на телефоне → бот → Reports  

Готово: отчёты Paper Desk в кармане.
