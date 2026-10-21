# OnvifSim: виртуальный 3D-мир, камеры в котором работают как ONVIF-устройства

> Temporary working plan (agreed 2026-10-13). Status: phases 1–6 and 2b done, 6b built and dry-run in CI (first tag, snap name and winget pending), plus roads and walking people from phase 7. Will be removed or turned into a roadmap once the MVP ships.

## Context
Нужен мультиплатформенный тестовый ONVIF-сервер. В браузерном редакторе собирается примитивный 3D-мир (рельеф, деревья, дома), в нём расставляются фиксированные и PTZ-камеры. Каждая камера становится отдельным ONVIF-устройством: отвечает на discovery, отдаёт RTSP H.264 с рендером мира и управляется по PTZ, обязательно с AbsoluteMove. Всё запускается в Docker. Основная польза: на этом стенде можно гонять OnvifLib, Probe, GUI, ODM и NVR без железа, детерминированно, в том числе в CI.

**Готовых решений нет.** Полную связку «3D-сцена → ONVIF PTZ» даёт только коммерческий Cambridge Pixel SPx Video Simulator. Open source закрывает куски: onvif_simple_server, go-onvif и IPyCam дают ONVIF/PTZ без 3D, MediaMTX-обёртки умеют «RTSP → ONVIF». Всё остальное придётся писать самим.

## Принятые решения
- Отдельный репозиторий `treealarm/OnvifSim`, уже склонирован в `/mnt/wdblue/tests/OnvifSim` (ветка `main`, remote origin по SSH, один коммит `Initial commit` с MIT LICENSE). Работаем там; пушим только по команде. OnvifLib подключается как NuGet только к тестам (клиент). На машине есть .NET 10.0.112, Node 20, ffmpeg, Docker.
- Один процесс на C# (.NET 10): веб, ONVIF, PTZ, RTSP, рендер. Редактор — React, но это статика в браузере; Node нужен только при сборке.
- Рендер — собственный программный растеризатор на C#, без OpenGL и нативных зависимостей. OpenGL/Unity — позже, за тем же интерфейсом.
- RTSP-сервер свой, in-process. MediaMTX не используем.
- Основной кодек — MJPEG, кодируется in-process; внешних бинарников у сервера нет. H.264 — опциональный профиль через ffmpeg, включается, если ffmpeg найден.

## Архитектура
```
Browser (React + react-three-fiber)  ──REST/SSE──►  OnvifSim.Server (ASP.NET Core, один процесс)
                                                         ├─ WorldStore       world.json в /data; генерация мешей (terrain, деревья, дома)
                                                         ├─ EditorApi        правки по частям мира, POST /api/meshes (меши объектов), SSE /api/events
                                                         ├─ ICameraRenderer  SoftwareRasterizer: мир + поза камеры → RGBA-кадр
                                                         ├─ CameraPipeline   кадры → JPEG (in-process) → RTP/JPEG; опционально ffmpeg → H.264 → RTP/H264
                                                         ├─ RtspServer       свой: OPTIONS/DESCRIBE/SETUP/PLAY/TEARDOWN, RTP/TCP-interleaved + UDP, Basic/Digest
                                                         ├─ OnvifHost        Kestrel-листенер на порт каждой камеры, SOAP-диспетчер
                                                         ├─ PtzSimulator     физика движения, маппинг пространств
                                                         └─ WsDiscovery      UDP 3702 multicast, ProbeMatch на каждую камеру
```

Структура репо: `src/OnvifSim.Server`, `src/OnvifSim.Rtsp` (RTSP-сервер и форматы RTP без знания о симуляторе; потоки ему отдаёт `CameraStreams` через `IRtspMediaSource`; отдельным пакетом — когда появится второй потребитель), `src/OnvifSim.Web`, `tests/OnvifSim.Tests`, `docker/`, `world.sample.json`.

### Мир
- **Единый JSON.** terrain (размер, сетка, heightmap), objects (type, transform, params, seed), cameras (fixed/ptz, позиция, монтаж, FOV, разрешение, fps, PTZ-лимиты и скорости, порт, логин/пароль, пресеты).
- **Геометрию объектов генерирует только сервер на C#.** Дерево = цилиндр + конусы, дом = box + призма-крыша, рельеф = grid с цветом по высоте и уклону. Освещение: направленный свет, туман, небо-градиент. Браузер получает готовые меши объектов (`POST /api/meshes`: JSON с base64-массивами, которые BufferGeometry берёт как есть; glTF и SharpGLTF оказались лишним слоем) и своего генератора деревьев и домов в TS не имеет; сетку рельефа строит сам по высотам, потому что кисть меняет её локально.
- **Вьюпорт редактора рендерит браузер** (орбита, гизмо, raycast, кисть — им нужны 60 fps без round-trip). **Превью «вид из камеры» рендерит сервер** — MJPEG от того же `ICameraRenderer`, что идёт в RTSP; пользователь видит ровно то, что получит ONVIF-клиент.
- **Кисть рельефа** деформирует сетку локально; по окончании мазка высоты уходят на сервер (`PUT /api/terrain`).

### Рендер
- `ICameraRenderer`: вход — мир и поза камеры (pan/tilt/fov), выход — RGBA-кадр. Реализация в MVP — `SoftwareRasterizer`: перспективная проекция, отсечение, z-буфер, Gouraud-шейдинг от направленного света, туман по глубине, небо-градиент. Brightness/contrast из Imaging применяются на выходе. Работает многопоточно по камерам; `Vector3`/`Vector4` из System.Numerics (SIMD).
- Оценка: сцена ~40k треугольников, кадр 640×360 — 5–10 мс на ядро; 8 камер по 15 fps — около одного ядра.
- Кадр детерминирован: удобно для тестов и для сравнения снимков.
- Если позже понадобится реализм (люди, машины, свет — например, для видеоаналитики), подключается бэкенд на OpenGL, Unity или Godot за тем же интерфейсом, не трогая ONVIF/PTZ/редактор.

### Стримы
- **MJPEG — основной профиль, всегда есть.** Кадр → baseline JPEG 4:2:0 со стандартными таблицами Хаффмана (StbImageWriteSharp при quality ≤ 90; если его вывод не подойдёт под RFC 2435 — свой baseline-энкодер ~300 строк) → RTP/JPEG по RFC 2435 (заголовки JFIF отбрасываются, в пакете type/Q/width/height, квант-таблицы в первом пакете кадра при Q=255). Ограничение RFC — стороны до 2040 px; для MVP хватает.
- **H.264 — опциональный профиль.** Включается, если найден ffmpeg с libx264 (`--ffmpeg`, `ONVIFSIM_FFMPEG` или PATH; `none` выключает): `ffmpeg -f rawvideo -pix_fmt rgba -s WxH -r fps -i pipe:0 -c:v libx264 -preset ultrafast -tune zerolatency -profile:v baseline -g GOV -x264-params aud=1:repeat-headers=1 -f h264 pipe:1`; кадры режутся по AUD (задержка один кадр), NAL → RTP по RFC 6184 (single NAL + FU-A), SPS/PPS в потоке перед каждым ключевым кадром и в SDP, когда уже известны. Свои настройки (`h264` в world.json: разрешение, fps, битрейт, GOV), профиль `Profile_2`/`VideoEncoder_2`, поток `/{camera}/h264`; новый зритель стартует с ближайшего ключевого кадра. Без ffmpeg профиль не объявляется в GetProfiles. H.265 — тот же путь с `libx265`, по надобности.
- **Параметры — свойство камеры.** Для каждой камеры в world.json и в редакторе задаются: разрешение, fps, качество JPEG, набор профилей, PTZ-лимиты и скорости, учётки, порт. Дефолт для MJPEG — 640×360 при 10 fps: плееры на ffmpeg (GUI, NVR) перед первым кадром набирают ~20 кадров, при 3 fps это ~7 с, при 10 — ~2 с; CPU ~5% ядра на камеру. Для каждого параметра камера хранит и допустимый диапазон, который отдаёт в `GetVideoEncoderConfigurationOptions`; `SetVideoEncoderConfiguration` меняет fps/разрешение/качество в этих пределах на лету (так делают ODM и NVR), изменения видны в редакторе.
- Трафик MJPEG заметно выше (~3–4 Мбит/с на камеру при 640×360@15 против ~300 кбит/с у H.264 на статичной сцене); для локалки и CI приемлемо, для десятков камер в 1080p нужен H.264.
- **Ресурсы по требованию:** пайплайн камеры жив, пока есть хотя бы одна RTSP-сессия в PLAY или открытое MJPEG-превью. Один рендер-кадр обслуживает все профили камеры. Snapshot рендерит один кадр и кодирует JPEG.
- RtspServer: один порт на все камеры (8554, пути `/cam1`, `/cam1/sub`) или порт на камеру — настраивается. Авторизация Basic/Digest теми же учётками, что ONVIF. Опционально SharpRTSP (MIT) для разбора сообщений и RTP-упаковки; иначе свой парсер.

### ONVIF
- **SOAP пишем сами, без CoreWCF** (CoreWCF плохо переносит UsernameToken-digest поверх HTTP без TLS и клиентов без SOAP Action). Диспетчер разбирает envelope, проверяет WS-UsernameToken digest (nonce + created + sha1, допуск по времени), роутит по namespace + имени элемента. Ответы сериализуются `XmlSerializer` на своих стабах, сгенерированных dotnet-svcutil из официальных WSDL ONVIF (github.com/onvif/specs, тег зафиксирован в `src/OnvifSim.Server/Onvif/Wsdl/generate.sh`; Device, Media, Media2, PTZ, Imaging; все операции — message contracts). Не из OnvifLib: стенд не должен разделять код с клиентом, которого проверяет. Ошибки — ONVIF faults (ter:NotAuthorized, ter:ActionNotSupported, ter:InvalidArgVal).
- **Каждой камере свой порт** (8001, 8002, …), путь `/onvif/device_service`, свой EndpointReference uuid, XAddrs из `ONVIFSIM_ADVERTISE_HOST`.
- **PTZ.** Состояние: pan°, tilt°, zoom 0..1 и предельные скорости по осям (ускорение не моделируется: движение с постоянной скоростью, позиция — функция времени, без тикающего цикла; в world.json пишется, когда движение закончилось). Generic space: pan [-1..1] ↔ [panMin..panMax] (при диапазоне ≥ 360° — бесконечный поворот, absolute идёт коротким путём), tilt [-1..1] ↔ [tiltMin..tiltMax], zoom [0..1] ↔ фокусное (логарифмически) → FOV; generic translation 1 = весь диапазон оси. Absolute, Relative, Continuous (с Timeout), Stop, GetStatus (MOVING/IDLE, UtcTime), пресеты, home. Пространства в градусах — позже.
- Объём MVP (Profile S + PTZ): Device (GetDeviceInformation, GetSystemDateAndTime, GetCapabilities, GetServices, GetServiceCapabilities, GetScopes, GetNetworkInterfaces, GetHostname, GetUsers); Media1 + Media2 (GetProfiles, GetStreamUri, GetSnapshotUri, GetVideoSources, GetVideoEncoderConfiguration(s)/Options, SetVideoEncoderConfiguration); PTZ (GetNodes/GetNode, GetConfigurations/Options, AbsoluteMove, RelativeMove, ContinuousMove, Stop, GetStatus, Get/Set/Goto/RemovePreset, Goto/SetHomePosition); Imaging минимум (GetImagingSettings/Options, brightness/contrast видны в картинке).

### Редактор (OnvifSim.Web)
Vite + React + TS + @react-three/fiber + drei + zustand. `dotnet build` собирает его через npm, когда меняются исходники, и кладёт в `wwwroot` рядом с бинарником; без Node.js сервер собирается и работает без редактора.
- Вьюпорт с OrbitControls. Кисти рельефа: raise, lower, smooth, flatten.
- Палитра объектов, установка raycast'ом на terrain, TransformControls, панель свойств.
- Камеры: гизмо фрустума, превью «вид из камеры» (MJPEG с сервера), джойстик и слайдеры PTZ через тот же PtzSimulator, что и ONVIF (REST); текущее направление приходит обратно в редактор по SSE.
- Кнопки «Сохранить» нет: каждая правка сразу уходит на сервер одной частью мира (`PUT/DELETE /api/objects/{id}`, `/api/cameras/{id}`, `PUT /api/terrain`, `/api/environment`), сервер проверяет её (400 с причиной) и применяет на лету: перестраивает сцену, перезапускает ONVIF-листенеры для добавленных и удалённых камер. Правки по частям не затирают того, что тем временем поменяли ONVIF-клиенты; позицию PTZ-головы, пресеты и home камера сохраняет свои. Изменения мира, порты и позиции PTZ редактор получает по SSE (`/api/events`): SignalR для одного направления не нужен.

## Языки и зависимости
Код: **C# (.NET 10)** — всё серверное; **TypeScript** — редактор.

Сервер (NuGet, пермиссивные лицензии):
- ASP.NET Core (Kestrel, SignalR) — в составе .NET.
- `StbImageWriteSharp` (public domain, чистый C#) — JPEG. ImageSharp не берём (платная лицензия для коммерческого использования).
- `SharpRTSP` (MIT) — опционально, парсер RTSP и RTP-упаковка; решение после прототипа в фазе 1.
- System.ServiceModel.Http — атрибуты и клиентские классы сгенерированных контрактов (`Onvif/Wsdl/OnvifContracts.cs`).
- xUnit; OnvifLib работает клиентом в тестах.

Внешние бинарники: у сервера обязательных нет. **ffmpeg** (libx264) — опционально, отдельным процессом, только для H.264-профиля; в Docker-образ кладём apt-пакет базового образа aspnet:10.0 (Ubuntu 24.04; стабильнее, чем пины на BtbN-автобилды, но это +425 МБ к образу), локально — по желанию.

Редактор (npm, MIT/Apache): `react`, `vite`, `typescript`, `three`, `@react-three/fiber`, `@react-three/drei`, `zustand`; Playwright для e2e. Панель свойств — свои поля, не `leva`.

## Фазы (по одному PR на фазу)
1. **Каркас и растеризатор.** Модель мира, генерация мешей, `SoftwareRasterizer`, snapshot JPEG одной фиксированной камеры, MJPEG-эндпоинт. Проверка производительности на целевой сцене — отсюда решение о разрешении/fps по умолчанию.
2. **RTSP + MJPEG.** Свой RtspServer (TCP-interleaved, UDP, Basic/Digest), RTP/JPEG. Проверка VLC, ffplay, go2rtc, GUI.
2b. **H.264 через ffmpeg** как опциональный профиль, отдельный PR после 2.
3. **ONVIF Device + Media1/2**, WS-Security, WS-Discovery, несколько камер на разных портах.
4. **PTZ** с симуляцией движения и маппингом пространств, плюс Imaging-минимум.
5. **Веб-редактор**: terrain, объекты, камеры, превью, PTZ-управление, live-обновление.
6. **Docker и CI**: multi-stage образ (node → dotnet publish → aspnet:10.0 + ffmpeg), amd64/arm64, `docker-compose.yml` с `network_mode: host` (Linux, discovery работает) и вариант с port mapping (Win/Mac, без discovery), публикация в ghcr и Docker Hub; `ci.yml`.
6b. **Пакеты и витрина**: single-file бинарник с открытием браузера, GitHub Release (zip, MSI), snap, winget, README/бейджи/баннер, PRIVACY/SECURITY/FUNDING/CONTRIBUTING, ffmpeg-mirror.
7. **Опционально, по надобности**: ~~движущиеся объекты по waypoint-путям~~ (сделано раньше, по запросу: дороги и люди, которые ходят по маршруту, по кругу или туда-обратно; положение — функция времени, кадр перерисовывается, только если человек в поле зрения камеры); машины по тем же маршрутам; ~~метаданные аналитики в RTSP~~ (сделано: люди в кадре рамками с ObjectId в дорожке vnd.onvif.metadata, точно по картинке, с учётом перекрытий; конфигурации метаданных и аналитики в Media/Media2; рамки поверх картинки в редакторе); ~~Events/PullPoint~~ (сделано: движение на VideoSource/MotionAlarm и RuleEngine/CellMotionDetector/Motion, пока аналитика видит человека; Renew/Unsubscribe, фильтр топиков); ~~ground truth для внешних тестов~~ (сделано: поза камеры и человека в HTTP API, мировые x/z в рамках аналитики — для проверки калибровки и наведения в VMS); зоны и линии (FieldDetector, LineDetector) из редактора; OSD-оверлей; день/ночь, шум; fault injection (задержки, 401, сдвиг часов, битые ответы; ~~обрыв сети камеры~~ — сделано вместе с Profile G); ~~Profile G~~ (сделано: Recording/Search/Replay, своя «SD-карта» с удержанием, прошлое перерисовывается, ONVIF replay по RTSP); OpenGL/Unity-бэкенд рендера.

## Дистрибуция и публичный проект
Цель — публичный open source наравне с OnvifLib: всё, что OnvifLib даёт пользователям, есть и здесь.

- **Форма поставки для десктопа.** `OnvifSim` — один self-contained бинарник (`PublishSingleFile`; редактор — папка `wwwroot` рядом с ним, а не ресурсы: zip, MSI и snap всё равно ставят папку, а встраивание потребовало бы своего файлового провайдера), при старте печатает URL редактора и открывает браузер; `--no-browser`, `--data <dir>`, `--port`. Так MSI/snap/winget ставят его как обычную программу, Docker не обязателен.
- **GitHub Release** по тегу `v X.Y.Z` (workflow `release.yml`, как `release-gui.yml`): linux-x64 и linux-arm64 zip, win-x64 zip, **WiX MSI** (`packaging/msi`, как `OnvifLib.Gui.Setup`), `workflow_dispatch` с input `tag` для пересборки.
- **Snap** `onvifsim` (`packaging/snap`, strict, `daemon: simple`, plugs `network`, `network-bind`; данные в `$SNAP_COMMON`): job в release-workflow публикует в stable через `snapcore/action-publish`. Имя регистрирует пользователь, секрет `SNAPCRAFT_STORE_CREDENTIALS` — тот же порядок, что для onviflib-gui.
- **winget** `treealarm.OnvifSim` (`packaging/winget`, ManifestVersion 1.12.0): после релиза обновляем манифесты и отправляем PR в microsoft/winget-pkgs из форка treealarm/winget-pkgs; отправка — только по команде.
- **Docker**: образ `ghcr.io/treealarm/onvifsim` и `treealarm/onvifsim` на Docker Hub, amd64+arm64, теги `latest` и `X.Y.Z`, публикуется тем же release-workflow; `docker-compose.yml` в репо.
- **ffmpeg** бандлится в zip/MSI/snap/Docker как в GUI (опционален для работы, но в пакетах есть). Пины BtbN протухают через ~2 недели — сразу зеркалим нужные архивы в релиз `ffmpeg-mirror` в treealarm/OnvifSim и ссылаемся на него.
- **CI** (`ci.yml`): build + xUnit + Playwright на push/PR; Docker-образ собирается на PR без публикации.
- **Репо как витрина**: README с баннером (`docs/images`), бейджами (release, Docker pulls, snap, license), гифкой редактора и таблицей «зачем это, если есть onvif_simple_server/go-onvif/IPyCam»; social preview; `LICENSE` MIT; `PRIVACY.md` (ничего не отправляет наружу), `SECURITY.md`, `.github/FUNDING.yml`, `CONTRIBUTING.md`, issue-шаблоны; GitHub topics (onvif, rtsp, ptz, simulator, docker, cctv, nvr-testing); `CHANGELOG.md`. Snap Store и Docker Hub описания — из README.
- **Версия** — в одном месте (`Directory.Build.props`), MSI/snap/Docker берут её из тега.

Все эти файлы — часть фазы 6 (Docker и CI) и новой фазы 6b: MSI, snap, winget, README/витрина. Snap и winget — после первого рабочего релиза, когда есть что ставить.

## Сложность и риски
- Растеризатор (1): ~600–800 строк, риск низкий, нативных зависимостей нет. Главное неизвестное — реальная скорость; фаза 1 её измеряет.
- RTSP (2): ~1000 строк, риск средний — совместимость с клиентами. Снижаем тестированием на 4–5 клиентах. RTP/JPEG: проверить, что вывод stb проходит у строгих декодеров (ffmpeg, VLC, go2rtc); запасной вариант — свой энкодер.
- ONVIF (3–4): объём средний, работа шаблонная, отладка совместимости с ODM/NVR.
- Редактор (5): самый большой по объёму, стандартный стек.
- Discovery в Docker: multicast только при host network на Linux; для Win/Mac документируем.

## Переиспользуем
- `OnvifLib` NuGet: `Camera` — клиент в интеграционных тестах (серверу не нужен).
- `samples/OnvifLib.Probe`: smoke-проверка симулятора целиком.
- `samples/OnvifLib.Gui`: ручная проверка PTZ и стримов.

## Verification
- Фаза 1: curl на snapshot, MJPEG в браузере; бенчмарк кадра на целевой сцене.
- Фаза 2: `ffplay rtsp://localhost:8554/cam1` (MJPEG), VLC (TCP и UDP), go2rtc, GUI; с авторизацией и без. Фаза 2b: то же для `/cam1/h264`; сервер без ffmpeg стартует и объявляет только JPEG-профиль.
- Фазы 3–4: OnvifLib.Probe против каждой камеры (все секции зелёные); xUnit-интеграционные тесты: сервер in-process, OnvifLib — клиент; AbsoluteMove → GetStatus (позиция достигнута, MOVING→IDLE), снимок до/после отличается. Ручная проверка — GUI и ODM, discovery видит N камер.
- Фаза 5: Playwright: добавить дерево и камеру, сохранить; камера появилась в discovery, на snapshot видно дерево.
- Фаза 6: `docker compose up` на чистой машине, затем те же проверки; сборка arm64 в CI.
- Фаза 6b: тег `v0.1.0` → все jobs зелёные, zip/MSI/образ скачиваются; MSI ставится на Windows и открывает редактор; snap ставится локально через `snap install --dangerous` и работает как сервис (discovery, RTSP, редактор); winget-манифест проходит `winget validate`.
