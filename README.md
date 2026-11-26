# Invest.Sign.Router

Сервис оркестрации подписания документов. Ведёт документы организации по маршрутам подписания: создаёт запросы на подпись
во внешнем ПО подписания, отслеживает их результат и переводит документ на следующий этап.

Сам сервис ничего не подписывает. Подписывает стороннее ПО — автоматически или руками сотрудников; оно же уведомляет
подписантов, кладёт подписанные файлы на общий ресурс и выставляет статусы в БД.

## Как это работает

> **Текущая схема (подписание через каталоги).** Состояние процесса подписания хранится в **Postgres**
> (проект `Invest.Sign.Router.PosDb`, EF Core code first). Oracle используется **только** для чтения новых документов
> из вьюхи `sign_incoming_document_v`. Путь документа:
>
> 1. Сервис опрашивает вьюху; для новой строки берёт имя файла из атрибута `file_name`.
> 2. Перемещает файл из `FileSigning:SourceDirectory` в `FileSigning:SigningDirectory` (каталог для ПО подписания).
> 3. Ждёт, пока файл и подпись `<имя>.sig` появятся в `FileSigning:SignedDirectory`.
> 4. Перемещает оба в `FileSigning:FinalDirectory` и сохраняет подпись в `sign_signature`.
>
> Статус каждого документа — строка `sign_document`, каждое изменение статуса — строка `sign_document_status_history`.
> История по идентификатору документа: `GET /api/documents/{docId}/status-history` (или
> `DocumentStatusService.GetStatusHistoryAsync(docId)` в коде), см. [HTTP API](#http-api).
> Ниже описана исходная схема с таблицами `SGN_*` в Oracle; её код (`Invest.Sign.Router.OracleDb`) сохранён, но хостом
> больше не используется.

```
 ПО подписания ──► sign_incoming_document_v ──┐
       ▲                                      │  опрос
       │  create_sign_request                 ▼
       │  cancel_sign_request         ┌────────────────┐      ┌──────────────┐
       └──────────────────────────────┤ Invest.Sign.   ├─────►│ SGN_* (схема │
       sign_request_v ───────────────►│ Router         │      │ сервиса)     │
                                      └───────┬────────┘      └──────────────┘
       общий ресурс (SMB) ── file_ref ────────┘ копия файла в BLOB
```

- **Только опрос БД.** Callback'ов и брокера сообщений нет; сервис — инициатор всех взаимодействий. Входящий HTTP —
  только чтение статусов ([HTTP API](#http-api)), на процесс подписания он не влияет.
- **Одна база Oracle** (19c или новее) и для таблиц сервиса, и для интеграции с ПО подписания. Внешние объекты описаны
  в [db/contract](db/contract/README.md), сервис обращается к внешней схеме только через них.
- **Несколько реплик.** Экземпляры маршрутов захватываются арендой (`locked_by`, `locked_until`) оптимистичным `UPDATE`;
  если реплика упала, аренда истекает сама. Время берётся из БД (`SYSTIMESTAMP`), а не из часов реплики.
- **Один шаг — одна транзакция.** Захват аренды, ровно один переход и освобождение аренды выполняются в одной транзакции.
  Вызовы внешних процедур идут в той же транзакции, что и запись в таблицы сервиса.
- **Подписанный файл копируется в BLOB** (`SGN_SIGNED_ARTIFACT`). Общий ресурс — транспорт, а не хранилище. Хэш и размер
  файла сверяются с `file_hash` и `file_size` из `sign_request_v`.

### Такт движка

Каждый такт `SigningEngine`:

1. Забирает новые документы из `sign_incoming_document_v` (до `DiscoveryBatchSize`) и создаёт для них экземпляры маршрутов.
   Маршрут выбирается по `doc_type`; документ неизвестного типа сразу получает статус `FAILED`.
2. По очереди обрабатывает готовые экземпляры (до `BatchSize`): вход в этап, создание запроса на подписание, опрос
   результата, сохранение подписанного файла, переход к следующему этапу или завершение маршрута.

Бизнес-исходы (отказ, истечение срока, ошибка подписания, несовпадение файла, ошибка условия) — обычные переходы.
Любое другое исключение считается технической ошибкой: шаг повторяется с экспоненциальной задержкой
(`RetryBaseDelay` … `RetryMaxDelay`), после `MaxAttempts` ошибок подряд маршрут завершается с `ATTEMPTS_EXHAUSTED`.

### Статусы

| Маршрут (`SGN_ROUTE_INSTANCE.status`) | Этап (`SGN_STAGE_INSTANCE.state`) |
|---|---|
| `ACTIVE` — в работе | `PENDING` — вход в этап выполнен, запрос ещё не создан |
| `COMPLETED` — все этапы подписаны | `REQUESTED` — запрос создан, ожидается результат |
| `REJECTED` — подписант отказал | `DONE` — подписано |
| `EXPIRED` — истёк срок этапа | `REJECTED` — отказ |
| `CANCELLED` — отменён | `EXPIRED` — истёк срок, запрос отменён через `cancel_sign_request` |
| `FAILED` — ошибка, повтор невозможен | `FAILED` — ошибка |

## Маршруты

Маршруты описываются в коде через fluent API. Этапы только последовательные; у этапа может быть условие входа
по атрибутам документа (JSON из колонки `attributes`).

```csharp
public static RouteDefinition ContractV1 { get; } =
    Route.For("contract", version: 1)
        .Stage("legal-review", s => s.Manual(signer: "legal").Timeout(TimeSpan.FromDays(3)))
        .Stage("org-seal", s => s.Automatic(signer: "org-seal").Timeout(TimeSpan.FromHours(1)))
        .Stage("director", s => s.Manual(signer: "director")
            .When(doc => doc.GetDecimal("amount") > 10_000)
            .Timeout(TimeSpan.FromDays(5)))
        .Build();
```

- `Automatic` / `Manual` передаются в ПО подписания как `p_kind` = `AUTO` / `MANUAL`, `signer` — как `p_signer`.
- `Timeout` — сколько этап может ждать результата в `REQUESTED`, отсчитывается с момента запуска этапа.
- Экземпляр маршрута запоминает код и версию. **Не редактируйте версию, по которой могли быть запущены документы** —
  добавьте новую рядом со старой.

Пока реальные типы документов неизвестны, по умолчанию регистрируются маршруты-примеры
[`SampleRoutes`](Invest.Sign.Router.Core/Routes/SampleRoutes.cs). Свои маршруты передаются в
`services.AddSigningRouter(routes)`.

### Действия этапов

Что делает этап, решает его обработчик ([`IStageHandler`](Invest.Sign.Router.Core/Abstractions/IStageHandler.cs)).
`Manual` / `Automatic` — сокращения для `SigningStageHandler`. Готовые обработчики из `Infrastructure`:

```csharp
Route.For("subscription", version: 1)
    .Stage("manual", s => s.CopyFile(targetFolder: "manual").Timeout(TimeSpan.FromDays(1)))
    .Stage("auto", s => s.Http(path: "subscriptions").Timeout(TimeSpan.FromMinutes(10)))
    .Build();

services.AddSigningRouter(routes)
    .AddCopyFileStage()   // секция CopyFile
    .AddHttpStage();      // секция HttpStage
```

- **`CopyFile`** копирует файл документа (путь — в атрибуте `file_ref` относительно `CopyFile:SourceRoot`) в каталог
  `CopyFile:TargetRoot/<targetFolder>` под именем `{docId}_{fileName}` и сразу завершает этап. Запись идёт через временный
  файл с атомарным переименованием, поэтому повтор безопасен.
- **`Http`** отправляет `POST <path>` с заголовком `Idempotency-Key: <request_id>` и телом
  `{ requestId, docId, docType, stageKey, attributes }`, затем опрашивает `GET <path>/<id>` и по истечении срока вызывает
  `DELETE <path>/<id>`. Ответ: `{ "status": "DONE" | "PENDING" | "REJECTED" | "FAILED", "id", "reason" }`.
  408, 429 и 5xx повторяются, остальные 4xx завершают маршрут с `FAILED` / `STAGE_ERROR`.

Свой обработчик подключается через `s.Handler<MyHandler>(settings)` и регистрируется в DI под своим типом
(`services.AddSingleton<MyHandler>()`). Как он устроен:

1. `StartAsync` / `CheckAsync` / `CancelAsync` выполняются **вне** транзакции: в них копируют файлы и вызывают HTTP.
   Они обязаны быть идемпотентны по `RequestId`, потому что после сбоя шаг повторяется.
2. `OnTransitionAsync` выполняется **внутри** транзакции шага, после проверки аренды. В нём пишут свои данные в БД,
   и они фиксируются вместе с переходом этапа.
3. `StartAsync` и `CheckAsync` возвращают исход: `Waiting` (этап в `REQUESTED`, опрос позже), `Done`, `Rejected` или
   `Failed`. Исключение считается технической ошибкой и повторяется.
4. `Data` из исхода сохраняется в `SGN_STAGE_INSTANCE.data`, и обработчик получает её при следующих вызовах. Например,
   там хранится идентификатор во внешней системе.

Если маршрут ссылается на незарегистрированный обработчик или обработчик отклонил настройки этапа (`ValidateSettings`),
хост не запустится. Любой вызов обработчика должен укладываться в `Engine:LeaseDuration`.

## Структура решения

| Проект | Назначение |
|---|---|
| `Invest.Sign.Router.Core` | Движок, модель, fluent API маршрутов, абстракции. Не зависит от Oracle и файловой системы |
| `Invest.Sign.Router.PosDb` | База состояния на Postgres (EF Core code first): документы, история статусов, подписи; unit of work, водяной знак обнаружения, чтение истории |
| `Invest.Sign.Router.OracleDb` | Чтение вьюхи входящих документов (`OracleIncomingDocumentFeed`); прежняя реализация хранилища и шлюза на Oracle |
| `Invest.Sign.Router.Infrastructure` | Обработчики этапов (подписание через каталоги, копирование файла, HTTP), чтение файлов с общего ресурса |
| `Invest.Sign.Router` | Хост (ASP.NET Core): DI, конфигурация, цикл тактов, HTTP API статусов |
| `Invest.Sign.Router.Tests` | Модульные тесты `Core` на фейках |
| `Invest.Sign.Router.IntegrationTests` | Тесты на реальном Oracle в контейнере (Testcontainers) |
| `db/` | SQL-скрипты: схема сервиса, спецификация внешнего контракта, заглушка внешней стороны — см. [db/README.md](db/README.md) |

Вся логика переходов живёт в `Core` и тестируется без БД.

## Требования

- .NET SDK 10
- Oracle 19c или новее
- Docker — только для интеграционных тестов

## База данных

### Postgres (состояние)

| Таблица | Содержимое |
|---|---|
| `sign_document` | Текущий статус документа: одна строка на документ — маршрут, аренда, текущий этап |
| `sign_document_status_history` | История изменения статусов, только вставка: событие + снимок статусов документа и этапа |
| `sign_signature` | Подписи (`bytea`, SHA-256, размер), одна на этап, только вставка |

Подписи — в отдельной таблице, а не в `sign_document`: у документа может быть несколько подписей (по одной на этап),
а двоичное содержимое не должно утяжелять строку, которую движок опрашивает и обновляет на каждом шаге.

Схема меняется только миграциями EF (`Invest.Sign.Router.PosDb/Migrations`); сервис при старте её не меняет.

```powershell
# новая миграция после изменения модели
dotnet ef migrations add <Name> --project Invest.Sign.Router.PosDb
# идемпотентный SQL-скрипт для DBA
dotnet ef migrations script --idempotent --project Invest.Sign.Router.PosDb -o db/postgres/schema.sql
# или применить напрямую
$env:SIGN_POSTGRES = "Host=...;Database=...;Username=...;Password=..."; dotnet ef database update --project Invest.Sign.Router.PosDb
```

Новые документы обнаруживаются по водяному знаку: вьюха Oracle читается начиная с самого позднего `created_at`
известного документа минус `Router:DiscoveryLookback`, уже известные документы отфильтровываются.

### Oracle

Сервис не меняет схему при старте: скрипты из `db/` накатывает Oracle-разработчик.

| Папка | Содержимое | От чьего имени |
|---|---|---|
| `db/service/` | Таблицы сервиса `SGN_*` | владелец схемы сервиса |
| `db/contract/` | Спецификация внешних вьюх и процедур (документ) | — |
| `db/stub/` | Заглушка внешней стороны для разработки и тестов | владелец внешней схемы |

Порядок настройки базы для разработки описан в [db/README.md](db/README.md).

## Конфигурация

Настройки читаются из `appsettings.json`, переменных окружения (`Oracle__ConnectionString` и т. п.) и user secrets.
Все секции проверяются при запуске: ошибка конфигурации останавливает хост.

| Ключ | По умолчанию | Описание |
|---|---|---|
| `Oracle:ConnectionString` | — | Строка подключения ODP.NET от имени владельца схемы сервиса. **Не храните в репозитории** |
| `Oracle:ExternalSchema` | — | Схема объектов внешнего контракта |
| `Oracle:CommandTimeoutSeconds` | `30` | Тайм-аут одной SQL-команды |
| `Postgres:ConnectionString` | — | Строка подключения Npgsql к базе состояния. **Не храните в репозитории** |
| `Postgres:CommandTimeoutSeconds` | `30` | Тайм-аут одной SQL-команды |
| `FileSigning:SourceDirectory` | — | Абсолютный путь: откуда берётся файл документа |
| `FileSigning:SigningDirectory` | — | Куда файл перемещается на подписание |
| `FileSigning:SignedDirectory` | — | Куда ПО подписания кладёт подписанный файл и подпись |
| `FileSigning:FinalDirectory` | — | Куда сервис перемещает подписанный файл и подпись |
| `FileSigning:SignatureSuffix` | `.sig` | Суффикс файла подписи: `doc.pdf` → `doc.pdf.sig` |
| `FileSigning:IoTimeout` | `00:01:00` | Максимальное время файловых операций одного шага |
| `Router:DiscoveryLookback` | `1.00:00:00` | Окно перечитывания вьюхи входящих до водяного знака |
| `SignedFiles:Root`, `SignedFiles:ReadTimeout` | — | Только с `AddSignedFiles()`: корень общего ресурса для обработчиков, возвращающих ссылку на файл |
| `CopyFile:SourceRoot`, `CopyFile:TargetRoot` | — | Только с `AddCopyFileStage()`: абсолютные корни исходных и целевых файлов |
| `CopyFile:CopyTimeout` | `00:01:00` | Максимальное время копирования одного файла |
| `HttpStage:BaseAddress` | — | Только с `AddHttpStage()`: адрес HTTP-сервиса этапов |
| `HttpStage:Timeout` | `00:00:30` | Тайм-аут одного HTTP-запроса |
| `Router:TickInterval` | `00:00:05` | Пауза между тактами, если такт не выбрал пачку целиком |
| `Router:ShutdownTimeout` | `00:01:00` | Сколько ждать завершения текущего шага при остановке |
| `Router:ReplicaName` | `POD_NAME` или имя машины | Основа идентификатора реплики; к ней добавляется случайный суффикс |
| `Engine:LeaseDuration` | `00:05:00` | Длительность аренды; должна превышать самый долгий шаг |
| `Engine:PollInterval` | `00:01:00` | Как часто опрашивается результат этапа в `REQUESTED` |
| `Engine:BatchSize` | `20` | Максимум экземпляров маршрутов за такт |
| `Engine:DiscoveryBatchSize` | `100` | Максимум новых документов за такт |
| `Engine:MaxAttempts` | `10` | Технических ошибок подряд до завершения маршрута |
| `Engine:RetryBaseDelay` | `00:00:30` | Задержка после первой технической ошибки, дальше удваивается |
| `Engine:RetryMaxDelay` | `00:30:00` | Верхняя граница задержки повтора |

Пример для локальной разработки:

```powershell
cd Invest.Sign.Router
dotnet user-secrets set "Oracle:ConnectionString" "User Id=sgn_service;Password=...;Data Source=localhost:1521/XEPDB1"
dotnet user-secrets set "Oracle:ExternalSchema" "SGN_EXT"
dotnet user-secrets set "Postgres:ConnectionString" "Host=localhost;Database=sign;Username=sign;Password=..."
```

В Kubernetes `terminationGracePeriodSeconds` должен быть больше `Router:ShutdownTimeout`.

## HTTP API

Адрес задаётся стандартно для ASP.NET Core: `ASPNETCORE_HTTP_PORTS`, `ASPNETCORE_URLS` или ключ `Urls`
(локально — `http://localhost:5080` из `launchSettings.json`). **Аутентификации нет**: эндпоинт отдаёт статусы и
историю любого документа, закрывайте его сетью или шлюзом.

`GET /api/documents/{docId}/status-history` — текущий статус документа и история его изменений.

- `200` — документ найден; `404` — сервис ещё не обнаружил документ с таким `doc_id`.
- `history` упорядочена по времени; `payload` — подробности события (JSON-объект).

```json
{
  "docId": "doc-1",
  "docType": "document",
  "status": "COMPLETED",
  "statusReason": null,
  "stageKey": "sign",
  "stageState": "DONE",
  "createdAt": "2026-11-26T10:00:00.12+00:00",
  "updatedAt": "2026-11-26T10:05:12.34+00:00",
  "history": [
    { "at": "2026-11-26T10:00:00.12+00:00", "eventType": "ROUTE_CREATED", "status": "ACTIVE",
      "statusReason": null, "stageKey": null, "stageState": null,
      "payload": { "docId": "doc-1", "docType": "document", "routeCode": "document", "routeVersion": 1 },
      "actor": "router-0-1a2b3c4d" },
    { "at": "2026-11-26T10:05:12.34+00:00", "eventType": "ROUTE_FINISHED", "status": "COMPLETED",
      "statusReason": null, "stageKey": "sign", "stageState": "DONE",
      "payload": { "status": "COMPLETED", "reason": null, "details": null },
      "actor": "router-0-1a2b3c4d" }
  ]
}
```

## Сборка и запуск

```powershell
dotnet build Invest.Sign.Router.slnx
dotnet run --project Invest.Sign.Router
```

Предупреждения компилятора и NuGet считаются ошибками (`Directory.Build.props`).

## Тесты

```powershell
dotnet test Invest.Sign.Router.slnx
```

- `Invest.Sign.Router.Tests` — модульные тесты движка и маршрутов, внешних зависимостей не требуют.
- `Invest.Sign.Router.IntegrationTests` — поднимают Oracle XE в Docker (`gvenzl/oracle-xe`), создают схемы `SGN_EXT`
  и `SGN_SERVICE` скриптами из `db/` и прогоняют сценарии движка на реальной базе, включая конкурентный захват.
  Если Docker недоступен, тесты Oracle пропускаются с указанием причины. Первый запуск дольше из-за загрузки образа.
