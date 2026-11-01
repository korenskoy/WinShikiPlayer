# WinShikiPlayer

Windows-перенос [MyShikiPlayer для macOS](https://github.com/korenskoy/myshikiplayer) на **C# / WinUI 3 / Windows App SDK 1.8**. Полное соответствие ещё не достигнуто. [Таблица соответствия](docs/PORTING_STATUS.md) перечисляет ограничения и оставшиеся работы.

Есть Shikimori OAuth, каталог и библиотека, карточки тайтлов, профиль, календарь, обсуждения, история, нативное видео Kodik. Работают ASS/libass и VTT, субтитры Anime365, сохранение позиции, синхронизация просмотра, качество, озвучки и серии. Ряд экранов и сценариев пока упрощён.

## Установка

Windows 10 2004 / 19041 или новее, **x64**. Для OAuth рекомендуется MSIX: `winshikiplayer://oauth` объявлен в манифесте и регистрируется при установке. Выбирать EXE через Microsoft Store не нужно.

Готовая сборка — в [GitHub Releases](https://github.com/korenskoy/WinShikiPlayer/releases/latest): скачайте `WinShikiPlayer-<версия>-Setup-x64.zip`, полностью распакуйте архив и запустите `Install.cmd`. После установки откройте WinShikiPlayer из меню «Пуск».

Публичные сборки не содержат ключей Shikimori/Kodik. На новом компьютере укажите собственные OAuth-данные Shikimori и API-токен Kodik в настройках приложения. Сборка подписана сертификатом разработки; установщик запрашивает разрешение администратора для добавления публичного сертификата, если он ещё не доверен.

Для сборки и установки из исходников:

```powershell
.\Build-MSIX.ps1
.\Install-MSIX.ps1 -TrustDevelopmentCertificate -IncludeLocalConfiguration
.\tools\Invoke-App.ps1 -KeepAlive
```

Пакет: `artifacts/installer/WinShikiPlayer-0.2.0-x64.msix`. Установщик использует проверенный локальный сертификат разработки с закреплённым отпечатком. Доверие устанавливается только с `-TrustDevelopmentCertificate`. Для публичного распространения нужен отдельный процесс подписи Windows-релизов.

Автономный комплект: распакуйте ZIP и запустите `Install.cmd`; исходники и инструменты разработки для установки не нужны. Личные ключи в комплект не включаются. Проверка без установки: `Install.ps1 -CheckOnly` внутри распакованного комплекта.

WinShikiPlayer появляется в меню «Пуск». Обновление MSIX сохраняет LocalState и Credential Locker. WebView2 Runtime нужен для резервного веб-плеера. Доступность видео зависит от источника, региона и кодеков Windows.

## Сборка установщика после обновлений

В PowerShell 7 из корня проекта:

```powershell
.\Build-Installer.ps1
```

Также можно запустить `Build-Installer.cmd`. Команда автоматически увеличивает номер сборки, запускает тесты, выполняет чистую Release-публикацию, подписывает и проверяет MSIX, затем создаёт ZIP в `artifacts/installer`. Старые ZIP сохраняются. Версия манифеста обновляется после успешной сборки; установка приложения не выполняется.

Для новой версии продукта: `./Build-Installer.ps1 -Version 0.3.0` (MSIX `0.3.0.0`). Подробности, параметры и состав результата — [docs/BUILD_INSTALLER.md](docs/BUILD_INSTALLER.md).

## Разработка

Нужны .NET SDK и Windows SDK; сборка проверена с .NET SDK 10.0.402, целевая платформа .NET 8.

```powershell
.\build.ps1 -Run
.\build.ps1 -Publish
dotnet run --project tests/WinShikiPlayer.Core.Tests
dotnet run --project tests/WinShikiPlayer.Core.Tests -- --live
```

Обычные тесты используют фикстуры и HTTP-моки; код возврата 1 означает ошибку. `--live` проверяет публичные API, GraphQL и получение потоков Kodik, не меняет аккаунт и не выводит ключи. Режимы приложения `--features-smoke`, `--session-smoke`, `--playback-smoke`, `--subtitle-smoke`, `--protocol-smoke` запускаются через `tools/Invoke-App.ps1` и записывают JSON по `--diagnostic-output=...`.

Публикация содержит .NET, Windows App SDK, XBF/PRI и x64 libass. Копировать нужно всю папку. ARM64 и публичный Windows-канал обновлений не подготовлены.

## Локальная конфигурация

Перед публикацией проверяйте содержимое коммита: [защита локальных данных](docs/REPOSITORY_PRIVACY.md). В проекте есть Gitleaks-правила и локальный pre-commit hook.

Скопируйте `appsettings.example.json` в **`appsettings.local.json` в корне** и заполните свои значения. Этот файл исключён из Git, публикация/MSIX его не включают.

```json
{
  "SHIKIMORI_CLIENT_ID": "ваш Client ID",
  "SHIKIMORI_CLIENT_SECRET": "ваш Client Secret",
  "SHIKIMORI_REDIRECT_URI": "winshikiplayer://oauth",
  "SHIKIMORI_BASE_URL": "https://shikimori.io",
  "SHIKIMORI_USER_AGENT": "WinShikiPlayer",
  "KODIK_API_TOKEN": "ваш токен"
}
```

Redirect URI должен точно совпадать с OAuth-приложением Shikimori, User-Agent — с его именем. Scopes: `user_rates topics comments`. Вход и разрешение браузера открыть приложение завершает пользователь.

Секреты и OAuth-токены хранятся в Windows Credential Locker. `-IncludeLocalConfiguration` копирует конфигурацию в LocalState пакета: `%LOCALAPPDATA%/Packages/WinShikiPlayer_c0mx6hg2yapzm/LocalState`. Unpackaged-сборка использует `%LOCALAPPDATA%/WinShikiPlayer`. Конфигурация переопределяет сохранённые настройки при запуске. Чтобы менять их только через интерфейс, уберите локальную конфигурацию из каталога данных/сборки и её исходную копию из корня.

## Структура и лицензии

- `src/WinShikiPlayer` — WinUI, Credential Locker, OAuth и libass overlay.
- `src/WinShikiPlayer.Core` — API/OAuth, Kodik, субтитры, фильтры, хранение.
- `tests/WinShikiPlayer.Core.Tests` — консольные тесты.
- `packaging` — MSIX; `tools` — запуск установленного пакета.
- `.reference` — игнорируемая копия macOS-исходника, для сборки не нужна.

Исходник, иконка и фикстуры используются по MIT, уведомление сохранено в `LICENSE`. Лицензии libass и зависимостей — `src/WinShikiPlayer/Licenses`. Алгоритм потоков перенесён из `KodikVideoLinksResolver.swift`. Неофициальный клиент Shikimori и Kodik.
