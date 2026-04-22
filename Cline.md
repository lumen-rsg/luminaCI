# Lumina CI — Микросервисная система сборки RPM-пакетов

## Обзор проекта

Lumina CI — микросервисная система для автоматизированной сборки, подписывания, сканирования и дистрибуции RPM-пакетов.

### Ключевые возможности
- Сборка RPM-пакетов в изолированных Docker-контейнерах
- PGP подписывание и верификация пакетов
- Подсчёт hashsum (SHA-256, SHA-512, MD5)
- CVE сканирование через внешнее API (Trivy/Grype)
- Веб-интерфейс управления (console.lumina.1t.ru)
- Управление репозиториями-зеркалами (packages.lumina.1t.ru)
- Аудит всех операций

---

## Архитектура

### Микросервисы

| Сервис | Порт | Назначение |
|--------|------|-----------|
| **Lumina.ApiGateway** | 5000 | API Gateway (YARP), аутентификация (LDAP+JWT) |
| **Lumina.BuildService** | 5001 | Управление сборкой, Pipeline, Docker-оркестрация |
| **Lumina.SecurityService** | 5002 | PGP подписывание, hashsum, управление ключами |
| **Lumina.ScannerService** | 5003 | CVE сканирование (Trivy/Grype API) |
| **Lumina.RepositoryService** | 5004 | Управление репозиториями, зеркало пакетов |
| **Lumina.WebApp** | 5005 | Blazor WebAssembly UI |

### Общая библиотека
- **Lumina.Shared** — DTO, модели, константы, события, расширения

---

## Инфраструктура

| Компонент | Порт | Назначение |
|-----------|------|-----------|
| PostgreSQL | 5432 | Основная БД |
| Redis | 6379 | Кэш, сессии |
| RabbitMQ | 5672/15672 | Message broker (MassTransit) |
| MinIO | 9000/9001 | S3-хранилище артефактов |
| Seq | 5341 | Централизованное логирование |
| Nginx | 80/443 | Reverse proxy |

---

## Технологический стек

- **.NET 10** (ASP.NET Core Minimal APIs)
- **Entity Framework Core** + PostgreSQL
- **Docker.NET** — управление контейнерами сборки
- **YARP** — API Gateway
- **MassTransit** + RabbitMQ — асинхронные сообщения
- **BouncyCastle** — PGP подписывание
- **SignalR** — real-time обновления
- **Blazor WebAssembly** + MudBlazor — UI
- **LDAP** (Novell.Directory.Ldap.NETStandard) — аутентификация
- **JWT** — авторизация

---

## Модели данных

### Pipeline
```
Pipeline
├── Id (Guid)
├── Name (string)
├── Description (string)
├── Status (PipelineStatus: Draft/Active/Paused/Archived)
├── Steps (List<PipelineStep>)
├── CreatedBy (string)
├── CreatedAt (DateTime)
├── UpdatedAt (DateTime)
└── Tags (List<string>)
```

### PipelineStep
```
PipelineStep
├── Id (Guid)
├── PipelineId (Guid)
├── Order (int)
├── Type (StepType: Build/Sign/Scan/Publish)
├── Name (string)
├── Configuration (Dictionary<string,string>)
└── Status (StepStatus: Pending/Running/Success/Failed/Skipped)
```

### BuildJob
```
BuildJob
├── Id (Guid)
├── PipelineId (Guid)
├── Status (BuildStatus: Queued/Building/Success/Failed/Cancelled)
├── SpecName (string)
├── SpecContent (string) — .spec файл
├── SourceUrl (string)
├── ContainerId (string)
├── Logs (string)
├── Artifacts (List<BuildArtifact>)
├── StartedAt (DateTime?)
├── CompletedAt (DateTime?)
└── TriggeredBy (string)
```

### BuildArtifact
```
BuildArtifact
├── Id (Guid)
├── BuildJobId (Guid)
├── FileName (string)
├── FilePath (string)
├── FileSize (long)
├── HashSha256 (string)
├── HashMd5 (string)
├── PgpSignature (string)
├── CveScanStatus (ScanStatus: Pending/Scanning/Clean/Vulnerable/Error)
└── StoragePath (string) — MinIO path
```

### SecurityKey
```
SecurityKey
├── Id (Guid)
├── KeyId (string) — PGP Key ID
├── KeyName (string)
├── PublicKey (string)
├── IsActive (bool)
├── CreatedAt (DateTime)
├── ExpiresAt (DateTime?)
└── CreatedBy (string)
```

### CveReport
```
CveReport
├── Id (Guid)
├── ArtifactId (Guid)
├── ScannerType (string) — Trivy/Grype
├── Status (ScanStatus)
├── Vulnerabilities (List<Vulnerability>)
├── ScannedAt (DateTime)
└── Summary (VulnerabilitySummary)
```

### AuditLog
```
AuditLog
├── Id (Guid)
├── Action (string)
├── EntityType (string)
├── EntityId (string)
├── PerformedBy (string)
├── Timestamp (DateTime)
├── Details (string)
└── IpAddress (string)
```

---

## Message Bus Events (RabbitMQ)

### Build Events
- `BuildJobCreated` — новая задача на сборку
- `BuildJobStarted` — сборка начата
- `BuildJobCompleted` — сборка завершена
- `BuildJobFailed` — ошибка сборки

### Security Events
- `PackageSigningRequested` — запрос на подпись
- `PackageSigned` — пакет подписан
- `HashComputed` — hashsum посчитан

### Scanner Events
- `CveScanRequested` — запрос на сканирование
- `CveScanCompleted` — сканирование завершено
- `VulnerabilityFound` — найдена уязвимость

### Repository Events
- `PackagePublishRequested` — запрос на публикацию
- `PackagePublished` — пакет опубликован
- `RepositoryUpdated` — репозиторий обновлён

---

## API Endpoints

### Build Service ( :5001 )
- `GET    /api/pipelines` — список пайплайнов
- `POST   /api/pipelines` — создание пайплайна
- `GET    /api/pipelines/{id}` — детали пайплайна
- `PUT    /api/pipelines/{id}` — обновление пайплайна
- `DELETE /api/pipelines/{id}` — удаление пайплайна
- `POST   /api/pipelines/{id}/trigger` — запуск пайплайна
- `GET    /api/builds` — список сборок
- `GET    /api/builds/{id}` — детали сборки
- `POST   /api/builds/{id}/cancel` — отмена сборки
- `GET    /api/builds/{id}/logs` — логи сборки (SignalR stream)

### Security Service ( :5002 )
- `GET    /api/keys` — список PGP ключей
- `POST   /api/keys` — добавление ключа
- `POST   /api/keys/{id}/revoke` — отзыв ключа
- `POST   /api/sign` — подпись пакета
- `POST   /api/verify` — верификация подписи
- `POST   /api/hash` — подсчёт hashsum

### Scanner Service ( :5003 )
- `POST   /api/scan` — запуск сканирования
- `GET    /api/reports` — список отчётов
- `GET    /api/reports/{id}` — детали отчёта
- `GET    /api/artifacts/{id}/vulnerabilities` — уязвимости артефакта

### Repository Service ( :5004 )
- `GET    /api/repositories` — список репозиториев
- `POST   /api/repositories` — создание репозитория
- `POST   /api/repositories/{id}/publish` — публикация пакета
- `POST   /api/repositories/{id}/sync` — синхронизация
- `GET    /api/packages` — список пакетов
- `GET    /api/packages/{id}` — детали пакета

---

## Docker Compose

### Сервисы
```yaml
# Infrastructure
postgres, redis, rabbitmq, minio, seq

# Application
api-gateway, build-service, security-service, 
scanner-service, repository-service, webapp
```

---

## Домены

- **console.lumina.1t.ru** — веб-интерфейс + API
- **packages.lumina.1t.ru** — зеркало пакетов

---

## Процесс сборки (Workflow)

```
1. Пользователь создаёт Pipeline через Web UI
2. Pipeline trigger → Build Service создаёт BuildJob
3. Build Service запускает Docker контейнер с rpmbuild
4. Контейнер собирает RPM → артефакт в MinIO
5. Security Service подписывает PGP + считает hashsum
6. Scanner Service проверяет на CVE
7. Если CVE чисто → Repository Service публикует в репозиторий
8. Pipeline → статус Success, уведомление пользователю
```

---

## Этапы разработки

- [x] Этап 1: Solution structure, shared library, Docker Compose, Cline.md
- [ ] Этап 2: Build Service (Docker RPM сборка, Pipeline engine)
- [ ] Этап 3: Security Service (PGP signing, hashsum)
- [ ] Этап 4: Scanner Service (CVE scanning via Trivy/Grype API)
- [ ] Этап 5: Repository Service (mirror management)
- [ ] Этап 6: API Gateway (YARP routing, LDAP auth + JWT)
- [ ] Этап 7: Web UI (Blazor WASM + MudBlazor)