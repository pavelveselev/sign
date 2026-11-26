# Invest.Sign.Router

Сервис оркестрации подписания документов. Ведёт документы организации по маршрутам подписания: создаёт запросы на подпись
во внешнем ПО подписания, отслеживает их результат и переводит документ на следующий этап.

Сам сервис ничего не подписывает. Подписывает стороннее ПО — автоматически или руками сотрудников; оно же уведомляет
подписантов, кладёт подписанные файлы на общий ресурс и выставляет статусы в БД.

## Как это работает

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

- **Только опрос БД.** Входящих HTTP-вызовов, callback'ов и брокера сообщений нет; сервис — инициатор всех взаимодействий.
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
| `Invest.Sign.Router.OracleDb` | Реализация абстракций на Oracle: хранилище экземпляров, шлюз к ПО подписания, источник входящих документов, unit of work (ODP.NET + Dapper) |
| `Invest.Sign.Router.Infrastructure` | Чтение подписанных файлов с общего ресурса |
| `Invest.Sign.Router` | Worker-хост: DI, конфигурация, цикл тактов |
| `Invest.Sign.Router.Tests` | Модульные тесты `Core` на фейках |
| `Invest.Sign.Router.IntegrationTests` | Тесты на реальном Oracle в контейнере (Testcontainers) |
| `db/` | SQL-скрипты: схема сервиса, спецификация внешнего контракта, заглушка внешней стороны — см. [db/README.md](db/README.md) |

Вся логика переходов живёт в `Core` и тестируется без БД.

## Требования

- .NET SDK 10
- Oracle 19c или новее
- Docker — только для интеграционных тестов

## База данных

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
| `SignedFiles:Root` | — | Абсолютный путь к корню общего ресурса с подписанными файлами |
| `SignedFiles:ReadTimeout` | `00:01:00` | Максимальное время чтения одного файла |
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
dotnet user-secrets set "SignedFiles:Root" "C:\\signed"
```

В Kubernetes `terminationGracePeriodSeconds` должен быть больше `Router:ShutdownTimeout`.

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
