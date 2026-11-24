# Privacy Policy / Политика конфиденциальности

**Effective date:** November 25, 2026

This policy describes how the GigaCleaner application (the "App") and its website handle data.

## 1. The App runs locally

GigaCleaner is a desktop utility for Windows. It performs cleaning and optimization **entirely on your computer**. No personal data, file names, or system information is collected, transmitted, or sold.

## 2. What the App accesses on your PC

The App needs local access to the following, only when you run the corresponding action:

- **Temporary files and caches** — to clean them (whitelist-based paths only);
- **Registry and Windows services** — to remove stale entries and manage startup (a System Restore point is created before changes);
- **winget / Windows Update** — to check for updates of installed programs and drivers (requires network for these checks only).

The App runs **only with your explicit action**: scanning, cleaning, optimizing, or checking updates.

## 3. Network requests

The App makes network requests in exactly three cases:

1. **Update checks for your apps** via winget and Windows Update — only when you open the "Updates" page or click "Check";
2. **Self-update check** — once at startup the App requests `https://tizz13.github.io/GigaCleaner/version.json` (a tiny JSON file with the latest version number and installer URL). It sends **no identifiers, no device info, and no usage data**;
3. **Downloading the new installer** — only after you confirm the update dialog.

All other operations are offline.

## 4. What the App does NOT do

- Does not collect analytics, telemetry, or crash reports;
- Does not track your activity;
- Does not read documents, photos, or messages;
- Does not share anything with third parties;
- Does not require an account or registration.

## 5. Data stored on your PC

Your settings (language, theme, schedule, hotkey, cookie whitelist) are stored locally in `%AppData%\GigaCleaner\settings.json`. This file never leaves your computer. When you uninstall, the App asks whether to delete these settings too.

## 6. The website (GitHub Pages)

The site at `https://tizz13.github.io/GigaCleaner/` is a static page hosted on GitHub Pages. GitHub's standard server logs (IP address, user agent) may be recorded by GitHub as for any public page. We do not use analytics or cookies on the site.

## 7. Changes to this policy

We may update this policy as the App evolves. The current version will always be published in this file.

## 8. Contact

Open an issue in the [repository](https://github.com/TIZZ13/GigaCleaner/issues) if you have questions about this policy.

---

*Никаких данных за пределы вашего компьютера не отправляется. Только проверка обновлений — и только по вашей команде.*