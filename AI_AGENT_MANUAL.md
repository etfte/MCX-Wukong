# MCX-Wukong: Полный мануал по установке и эксплуатации для ИИ-Агента (AI Agent Runbook)

## 1. Введение и Архитектура Системы

**MCX-Wukong** (v0.2.0) — это межпроцессный гибридный мод, объединяющий **Black Myth: Wukong** (Unreal Engine 5) и **Minecraft** (Fabric 1.21.4).

### Компоненты системы
1. **Minecraft Instance (Fabric 1.21.4)**:
   - Моды: `MCX-Wukong-Bridge-0.2.0.jar`, Fabric API, BetterCombat, Modern Guns и др.
   - Транспорт: Открывает локальный WebSocket-сервер на порту `127.0.0.1:25599`.
   - Видеопоток: Экспортирует фреймбуфер мира, глубины и оверлея (интерфейса/руки) в общую разделяемую память `Local\MCPassthroughFrame` с помощью кольцевого буфера PBO (3 слота).
   - Мир: Предустановленный мир `MCX-Wukong`.
2. **Black Myth: Wukong Client**:
   - `b1-Win64-Shipping.exe`.
   - **CSharpLoader**: Инжектирует Mono 4.0 runtime в процесс Wukong и загружает мод `DqjCombat.dll` (`b1\Binaries\Win64\CSharpLoader\Mods\DqjCombat\DqjCombat.dll`).
   - **ReShade с поддержкой Addon'ов**:
     - `dxgi.dll` (ReShade runtime).
     - `DqjMCCompositor.addon64`: Нативный C++ аддон ReShade, читающий `Local\MCPassthroughFrame` и передающий текстуры в шейдер.
     - `MCPassthrough.fx`: ReShade-шейдер, накладывающий HUD и 3D-мир Minecraft на рендер Wukong, подавляя родной HUD Unreal Engine.
     - `DqjMCBridge.ini`: Пресет ReShade.
     - `DqjFrameMatch.ini`: Файл управления синхронизацией кадров (отключение задержек).

---

## 2. Критические подводные камни и баги (Reverse Engineering & Solutions)

Если ИИ-агент выполняет автоматическую установку "по стандартной инструкции", мод гарантированно **НЕ заработает** на 99% систем без применения следующих исправлений:

### Баг №1: Локаль Windows (Русская/Европейская) — Ошибка `world-anchor-error` и `acknowledgement timeout`
- **Суть проблемы**:
  В исходном `DqjCombat.dll` методы `McBridgeWire.Number` и `McWorldCatalog.N` парсят вещественные числа из JSON через:
  ```csharp
  private static double Number(JsonData data) => double.Parse(data.ToString(), CultureInfo.InvariantCulture);
  ```
  В русскоязычной Windows вызов `data.ToString()` для чисел с плавающей запятой форматирует их через запятую `,` (например, `"4,115371"` или `"-49597,6171875"`). Затем `double.Parse(..., InvariantCulture)` принимает запятую за **разделитель тысяч**!
  В результате:
  - Координата `4.11` превращается в `4115371.0` (4 миллиметра -> 4 миллиона метров).
  - Координата `-49597.61` в `world-anchors.json` превращается в `-495976171875.0` (минус 495 миллиардов).
  - Метод `McWorldCoordinates.RequireSpawn` выбрасывает исключение:
    ```text
    world-anchor-error | System.InvalidOperationException: Native destination is outside this MC region's verified bounds
    ```
  - Рукопожатие `McGroundPreparation.Ready` сравнивает `4115371.0 - 4.11 > 0.15` и бесконечно падает с тайм-аутом:
    ```text
    bridge-ground-error | prepare aborted: acknowledgement timeout
    ```
- **Решение**:
  Необходимо пропатчить IL-байткод `DqjCombat.dll`, добавив `.Replace(',', '.')` перед `double.Parse`, а также в `Mod.Init` принудительно выставить `CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture`.
  Готовый пропатченный файл находится в [patches/DqjCombat.patched.dll](file:///patches/DqjCombat.patched.dll) (и в `payload/DqjCombat.dll`).
  Исходный код утилиты для патчинга находится в [tools/InspectTool/Program.cs](file:///tools/InspectTool/Program.cs).

### Баг №2: Сборка патчера под .NET 6+ ломает Mono в CSharpLoader
- **Суть проблемы**:
  Если пересобирать или патчить `DqjCombat.dll` с помощью утилиты на базе современного .NET (Core 3.1 / 6 / 7 / 8 / 9), Mono.Cecil автоматически добавляет ссылку на сборку `System.Private.CoreLib 6.0.0.0`.
  Встроенный в CSharpLoader движок Mono 4.0 не знает про `System.Private.CoreLib` и аварийно завершает загрузку мода с фатальной ошибкой:
  ```text
  Could not load file or assembly 'System.Private.CoreLib, Version=6.0.0.0' or one of its dependencies.
  ```
- **Решение**:
  Любые инструменты модификации сборок для CSharpLoader должны компилироваться строго под **`.NET Framework 4.8`** (где базовой библиотекой является `mscorlib 4.0.0.0`).

### Баг №3: Ощущение 30/10 FPS в Minecraft при высоком FPS в Wukong
- **Суть проблемы**:
  1. Аддон `DqjMCCompositor.addon64` по умолчанию включает синхронизацию `[frame-match]` (`lag=0`). Если кадр Minecraft опаздывает хоть на миллисекунду, аддон выбрасывает кадр (`missed`) или задерживает старый (`held`), отбрасывая до 90% кадров.
  2. В Minecraft 1.21+ активен `FramerateLimitTracker`: если окно Minecraft не получает прямой ввод более 60 секунд (так как игрок держит фокус на Wukong), игра переходит в режим AFK и аппаратно зажимает FPS до **30 кадров/сек**, а при сворачивании окна — до **10 кадров/сек**.
- **Решение**:
  1. В папке `b1\Binaries\Win64` необходимо создать файл [DqjFrameMatch.ini](file:///patches/DqjFrameMatch.ini) со словом `off`. Это отключает задержки и поднимает передачу до **100+ FPS**.
  2. Окно Minecraft **нельзя сворачивать на панель задач** — оно должно оставаться открытым на заднем плане под окном игры.

### Баг №4: Смерть Стива и падение в пустоту (`hp=0`)
- **Суть проблемы**:
  Если игрок в Wukong забегает в пещеру или локацию с перепадом высот быстрее, чем Minecraft сгенерировал чанки, Стив падает за пределы мира (`etft3 fell out of the world`).
  При этом в окне Minecraft открывается экран смерти (`DeathScreen`), здоровье падает до `0.0`. При `hp=0` мост Wukong не может привязать позицию (`ready=False`), скрывает интерфейс Minecraft и возвращает родной интерфейс Wukong.
- **Решение**:
  Необходимо в окне Minecraft нажать кнопку **«Возродиться» (Respawn)** (либо отправить эмуляцию клика по центру экрана), после чего в Wukong сделать один шаг (клавиша `W`).

---

## 3. Пошаговая инструкция для ИИ-Агента

### Этап 1: Подготовка сборки Minecraft в Prism Launcher
1. Убедиться, что установлен Prism Launcher (каталог `AppData\Roaming\PrismLauncher`).
2. Распаковать инстанс `MCX-Wukong` в `AppData\Roaming\PrismLauncher\instances\MCX-Wukong`.
   - Внутри должен быть `instance.cfg` (версия 1.21.4, Fabric Loader).
   - В папке `.minecraft\mods` должны лежать:
     - `MCX-Wukong-Bridge-0.2.0.jar`
     - `fabric-api-0.119.4+1.21.4.jar`
     - `bettercombat-fabric-2.3.0+1.21.4.jar`
     - `modern-guns-V1.5-data.jar`
   - В папке `.minecraft\saves` должен присутствовать мир `MCX-Wukong`.
3. Запустить процесс Minecraft:
   ```powershell
   # Запуск через Prism Launcher CLI или напрямую через javaw
   prismlauncher --launch "MCX-Wukong"
   ```
4. Убедиться, что мир загружен и сокет слушает:
   ```powershell
   Get-NetTCPConnection -LocalPort 25599 -ErrorAction SilentlyContinue
   ```
   Должно возвращаться состояние `Listen`.

### Этап 2: Установка компонентов в Black Myth: Wukong
Путь к исполняемым файлам игры: `<SteamLibrary>\steamapps\common\BlackMythWukong\b1\Binaries\Win64`.

1. Скопировать файлы из `payload`:
   - `dxgi.dll` -> `b1\Binaries\Win64\dxgi.dll`
   - `DqjMCCompositor.addon64` -> `b1\Binaries\Win64\DqjMCCompositor.addon64`
   - `DqjMCBridge.ini` -> `b1\Binaries\Win64\DqjMCBridge.ini`
   - `DqjFrameMatch.ini` -> `b1\Binaries\Win64\DqjFrameMatch.ini`
   - `MCPassthrough.fx` -> `b1\Binaries\Win64\reshade-shaders\Shaders\MCPassthrough.fx`
   - `ReShade.fxh` -> `b1\Binaries\Win64\reshade-shaders\Shaders\ReShade.fxh`
2. Скопировать CSharpLoader и мод:
   - `CSharpLoader.dll` и сопутствующие файлы -> `b1\Binaries\Win64\`
   - Патченный `DqjCombat.dll` -> `b1\Binaries\Win64\CSharpLoader\Mods\DqjCombat\DqjCombat.dll`

### Этап 3: Запуск и валидация
1. Запустить игру:
   ```powershell
   Start-Process "steam://rungameid/2358720"
   ```
2. В главном меню нажать **«Продолжить путешествие»**.
3. Проверить лог `b1\Binaries\Win64\CSharpLoader\Mods\DqjCombat\dqj-combat.log`:
   - Должно быть:
     ```text
     bridge-link | real Minecraft 1.21.4 connected
     bridge-ground | MC spawn column placed at ...
     bridge-hud | MC HUD hidden=False
     bridge | real MC authority enabled
     bridge | MC frame=...
     ```
4. В [ReShade.log](file:///D:/SteamLibrary/steamapps/common/BlackMythWukong/b1/Binaries/Win64/ReShade.log) проверить:
   ```text
   [frame-match] config enabled=0 lag=0
   ```
5. Сделать скриншот окна игры и убедиться, что виден хотбар, сердечки и рука Стива.

---

## 4. Скрипт проверки состояния для ИИ-Агента (Healthcheck)

ИИ-агент может выполнить следующий Python-скрипт для быстрой диагностики:

```python
import socket, json

def check_mc_bridge():
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        s.settimeout(2.0)
        s.connect(('127.0.0.1', 25599))
        req = (
            "GET / HTTP/1.1\r\nHost: 127.0.0.1:25599\r\nUpgrade: websocket\r\n"
            "Connection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"
            "Sec-WebSocket-Version: 13\r\n\r\n"
        )
        s.sendall(req.encode('utf-8'))
        s.recv(1024); s.recv(1024)
        
        # Запрос состояния
        payload = json.dumps({'t': 'state'}).encode('utf-8')
        frame = bytearray([0x81, 0x80 | len(payload), 0x12, 0x34, 0x56, 0x78])
        frame.extend([b ^ frame[-4 + (i % 4)] for i, b in enumerate(payload)])
        s.sendall(frame)
        
        raw = s.recv(8192)
        b1 = raw[1] & 0x7F
        offset = 2 if b1 < 126 else (4 if b1 == 126 else 10)
        length = b1 if b1 < 126 else int.from_bytes(raw[2:offset], 'big')
        data = json.loads(raw[offset:offset+length].decode('utf-8', errors='ignore'))
        
        print(f"[OK] Minecraft Bridge: Ready={data.get('ready')}, Health={data.get('health')}, Screen={data.get('screen')}")
        s.close()
    except Exception as e:
        print(f"[FAIL] Minecraft Bridge offline: {e}")

if __name__ == "__main__":
    check_mc_bridge()
```
