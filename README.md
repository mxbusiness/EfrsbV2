# EfrsbV2

Вторая версия проекта для мониторинга ЕФРСБ.

## Что внутри

- `Efrsb.Web` — ASP.NET Core + Blazor Server + API.
- `Efrsb.Desktop` — desktop-клиент, который ходит только в ваш сервер.
- `Efrsb.Domain` — сущности.
- `Efrsb.Application` — бизнес-логика отслеживания компаний и сообщений.
- `Efrsb.Infrastructure` — PostgreSQL, EF Core, Fedresurs REST client, Hangfire.
- `Efrsb.Contracts` — DTO для Web/Desktop.

## Безопасность Fedresurs

Логин и пароль Fedresurs **не должны попадать в desktop-клиент и в git**. Они задаются только на сервере.

Локально лучше использовать user-secrets:

```bash
cd src/Efrsb.Web
dotnet user-secrets init
dotnet user-secrets set "Fedresurs:BaseUrl" "https://bank-publications-prod.fedresurs.ru"
dotnet user-secrets set "Fedresurs:Login" "ВАШ_LOGIN"
dotnet user-secrets set "Fedresurs:Password" "ВАШ_PASSWORD"
dotnet user-secrets set "Jwt:Key" "очень-длинный-секрет-минимум-32-символа"
```

На сервере используйте environment variables:

```bash
Fedresurs__BaseUrl=https://bank-publications-prod.fedresurs.ru
Fedresurs__Login=...
Fedresurs__Password=...
Jwt__Key=...
```

## PostgreSQL

В `src/Efrsb.Web/appsettings.json` сейчас стоит локальная строка:

```json
"DefaultConnection": "Host=localhost;Port=5432;Database=efrsb_v2;Username=postgres;Password=postgres"
```

## Запуск

```bash
cd src/Efrsb.Web
dotnet restore
dotnet run
```

После запуска:

- Web UI: `https://localhost:5001`
- Hangfire dashboard: `/jobs`

## Первый сценарий тестирования

1. Открыть `/register`.
2. Создать пользователя.
3. Перейти в `Компании`.
4. Добавить компанию по ИНН/ОГРН/названию/GUID.
5. Нажать `Обновить`.
6. Открыть сообщения.
7. Проверить unread/read состояние.

## Что реализовано в первой сборке

- регистрация/вход по email/password;
- роли `Admin`, `User`;
- хранение пользователей в PostgreSQL через ASP.NET Core Identity;
- серверный Fedresurs client;
- хранение Fedresurs XML в БД;
- скачивание ZIP-файлов сообщений сразу при синхронизации;
- отслеживаемые компании на пользователя;
- состояние read/unread на пользователя;
- Blazor Server web UI;
- desktop-клиент без Fedresurs-секретов.

## Следующие задачи

- добавить нормальный UI ошибок;
- добавить страницу графа связей;
- добавить дашборд по типам сообщений;
- добавить выбор периода синхронизации;
- добавить просмотр и скачивание файлов из web UI;
- добавить полноценную обработку XML по типам сообщений.
