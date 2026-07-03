# Lumina CI

Микросервисная система непрерывной сборки RPM-пакетов. Автоматически собирает, подписывает (PGP), сканирует на уязвимости (CVE) и публикует пакеты в репозиторий.

## Системные требования

- **ОС**: Linux (RHEL/CentOS/ALTLinux/Ubuntu)
- **Docker**: >= 24.0
- **Docker Compose**: >= 2.20 (плагин `docker compose`)
- **RAM**: минимум 4 ГБ, рекомендуется 8 ГБ
- **Диск**: минимум 20 ГБ свободного места
- **Порты**: 80, 443 (основной доступ), 5432, 5672, 6379, 9000 (инфраструктура)

## Быстрый старт

### 1. Клонировать репозиторий

```bash
git clone <url-репозитория> lumina-ci
cd lumina-ci
```

### 2. Настроить переменные окружения

```bash
cd deploy
cp .env.example .env    # если нет .env — создать вручную (см. ниже)
```

Минимальный `.env`:

```env
POSTGRES_PASSWORD=lumina_dev_2024
RABBITMQ_PASSWORD=lumina_rmq_2024
MINIO_USER=luminaadmin
MINIO_PASSWORD=lumina_minio_2024
JWT_SECRET=lumina_jwt_dev_secret_key_2024_min32chars!!
```

### 3. Запустить все сервисы

```bash
docker compose up -d --build
```

Первый запуск займёт 5-10 минут (сборка образов .NET, загрузка базовых образов).

### 4. Проверить запуск

```bash
# Все сервисы должны быть Up/Healthy
docker compose ps

# Проверить API
curl -s http://localhost/api/pipelines?page=1 | jq '.success'
# Ожидаемый ответ: true

# Открыть веб-интерфейс
# http://localhost
```

### 5. Готово!

Веб-интерфейс доступен на `http://localhost`.

---

## Конфигурация

### Переменные `.env`

| Переменная | По умолчанию | Описание |
|---|---|---|
| `POSTGRES_PASSWORD` | `lumina_dev_2024` | Пароль PostgreSQL |
| `RABBITMQ_PASSWORD` | `lumina_rmq_2024` | Пароль RabbitMQ |
| `MINIO_USER` | `luminaadmin` | Логин MinIO (S3 хранилище) |
| `MINIO_PASSWORD` | `lumina_minio_2024` | Пароль MinIO |
| `JWT_SECRET` | `lumina_jwt_dev...` | Секретный ключ JWT (мин. 32 символа) |
| `LDAP_HOST` | `localhost` | Адрес LDAP сервера (опционально) |
| `LDAP_PORT` | `389` | Порт LDAP |
| `LDAP_BASE_DN` | `dc=lumina,dc=1t,dc=ru` | Base DN для LDAP |

### DNS (для production)

Для работы по доменным именам добавьте в `/etc/hosts` или настройте DNS:

```
<IP-сервера>  console.lumina.1t.ru
<IP-сервера>  packages.lumina.1t.ru
```

---

## Использование

### Веб-интерфейс

Откройте `http://localhost` в браузере.

**Страницы:**
- **Dashboard** — обзор системы, статистика
- **Pipelines** — управление пайплайнами сборки
- **Builds** — история и статус сборок
- **Security** — управление ключами PGP

### Создание пайплайна

1. Откройте **Pipelines** → **+ New Pipeline**
2. Заполните поля:
   - **Name** — название (например `my-package-build`)
   - **Description** — описание
   - **Tags** — теги через запятую (опционально)
3. Настройте **Git Integration** (опционально):
   - **Git Repository URL** — URL репозитория (`.git`)
   - **Branch** — ветка (по умолчанию `main`)
   - **Spec File Path** — путь к `.spec` файлу в репозитории
   - **Webhook Secret** — секрет для верификации webhook
4. Нажмите **Create**

### Ручной запуск сборки

1. На странице **Pipelines** нажмите **▶ Run** рядом с пайплайном
2. Заполните форму:
   - **Package Spec Name** — имя пакета
   - **Source URL** — ссылка на исходники (tar.gz или git://)
   - **RPM Spec Content** — содержимое `.spec` файла
   - **Triggered By** — ваше имя
3. Нажмите **▶ Run Build**

### Автоматическая сборка по Git Push

После создания пайплайна с Git интеграцией, настройте webhook в вашем Git-хостинге:

**URL webhook:** `http://<IP-сервера>/api/webhooks/<pipeline-id>`

Pipeline ID можно узнать в ответе API:
```bash
curl -s http://localhost/api/pipelines?page=1 | jq '.data.pipelines[] | select(.name=="my-package-build") | .id'
```

**Настройка в GitHub:**
1. Repository → Settings → Webhooks → Add webhook
2. Payload URL: `http://<IP>/api/webhooks/<pipeline-id>`
3. Content type: `application/json`
4. Secret: тот же, что указан в настройках пайплайна
5. Events: **Just the push event**

**Настройка в GitLab:**
1. Project → Settings → Webhooks
2. URL: `http://<IP>/api/webhooks/<pipeline-id>`
3. Secret token: webhook secret из настроек пайплайна
4. Trigger: **Push events**

**Настройка в Forgejo/Gitea:**
1. Repository → Settings → Webhooks → Add webhook
2. Target URL: `http://<IP>/api/webhooks/<pipeline-id>`
3. Secret: webhook secret
4. Trigger: **Push events**

### Скачивание Spec файла

На странице деталей сборки (`/builds/<id>`) нажмите кнопку **📄 Download Spec**.

---

## API Reference

### Аутентификация

```bash
# Получить JWT токен
curl -X POST http://localhost/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"admin"}'
```

Для защищённых endpoint'ов добавляйте заголовок:
```
Authorization: Bearer <токен>
```

### Pipelines

| Метод | Endpoint | Описание |
|---|---|---|
| `GET` | `/api/pipelines?page=1&pageSize=20` | Список пайплайнов |
| `GET` | `/api/pipelines/{id}` | Детали пайплайна |
| `POST` | `/api/pipelines` | Создать пайплайн |

### Builds

| Метод | Endpoint | Описание |
|---|---|---|
| `GET` | `/api/builds?page=1&pageSize=20` | Список сборок |
| `GET` | `/api/builds/{id}` | Детали сборки (артефакты, логи) |
| `GET` | `/api/builds/{id}/spec` | Скачать .spec файл |
| `POST` | `/api/pipelines/{id}/trigger` | Запустить сборку вручную |

### Webhooks

| Метод | Endpoint | Описание |
|---|---|---|
| `POST` | `/api/webhooks/{pipelineId}` | Webhook для git push |

### Security

| Метод | Endpoint | Описание |
|---|---|---|
| `POST` | `/api/security/sign` | Подписать пакет (PGP) |
| `POST` | `/api/security/hash` | Вычислить SHA256/MD5 |

### Scanner

| Метод | Endpoint | Описание |
|---|---|---|
| `POST` | `/api/scanner/scan` | CVE сканирование пакета |

---

## Устранение неполадок

### 502 Bad Gateway

```bash
# Проверить статус всех сервисов
docker compose ps

# Перезапустить nginx и gateway
docker compose restart nginx api-gateway

# Проверить логи
docker compose logs api-gateway --tail 20
docker compose logs nginx --tail 20
```

### Сервисы не запускаются

```bash
# Проверить логи конкретного сервиса
docker compose logs build-service --tail 50
docker compose logs postgres --tail 20

# Пересобрать все образы
docker compose up -d --build
```

### Ошибка подключения к PostgreSQL

```bash
# Проверить что PostgreSQL healthy
docker compose ps postgres

# Если не healthy — перезапустить
docker compose restart postgres

# Проверить подключение
docker compose exec postgres psql -U lumina -d lumina_ci -c "SELECT 1"
```

### Сборка зависла (статус Building)

```bash
# Список активных сборок
curl -s http://localhost/api/builds?page=1 | jq '.data.builds[] | select(.status==2)'

# Проверить Docker контейнеры сборки
docker ps --filter "name=rpm-build-" 

# Удалить зависший контейнер
docker rm -f <container-id>
```

### Не работает webhook

1. Убедитесь что Pipeline ID правильный (GUID, не имя)
2. Проверьте что webhook secret совпадает
3. Проверьте логи: `docker compose logs build-service --tail 50`

### Полный перезапуск (сброс данных)

```bash
# ⚠️ Удалит ВСЕ данные (БД, файлы, кэш)
docker compose down -v
docker compose up -d --build
```

---

## Управление сервисами

```bash
# Запуск
docker compose up -d

# Остановка
docker compose down

# Перезапуск одного сервиса
docker compose restart build-service

# Пересборка после изменений кода
docker compose up -d --build build-service

# Логи
docker compose logs -f build-service
docker compose logs -f api-gateway
```

---

## Учетные данные

В Lumina CI **нет учётных данных по умолчанию**. Перед первым запуском задайте все
секреты в `.env` (скопируйте `deploy/.env.example`):

- `ADMIN_PASSWORD` — пароль начальной учётки администратора (создаётся при первом
  запуске, когда таблица пользователей пуста; без него api-gateway не стартует).
  Необязательные `ADMIN_USERNAME` (по умолчанию `admin`), `DEVELOPER_USERNAME` /
  `DEVELOPER_PASSWORD` (учётка разработчика создаётся только если задан пароль).
- `JWT_SECRET` — ключ подписи JWT (мин. 32 символа).
- `GPG_PASSPHRASE` — парольная фраза PGP-ключа подписи RPM.
- `POSTGRES_PASSWORD`, `RABBITMQ_PASSWORD`, `MINIO_PASSWORD` (и `MINIO_USER`).

Пароли пользователей хранятся в БД в виде BCrypt-хэшей. Если переменная не задана,
`docker compose up` завершится с ошибкой, а не откатится на дев-дефолт.

> ⚠️ **Для production обязательно смените все пароли в `.env`!**