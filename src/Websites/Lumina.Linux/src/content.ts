export type Language = "en" | "ru";
export type SupportTier = "platinum" | "gold";

export type Board = {
  name: string;
  tier: SupportTier;
  tierLabel: string;
  useCase: string;
  description: string;
  facts: string[];
};

export type Feature = {
  index: string;
  title: string;
  description: string;
};

export type VerificationLine = {
  label: string;
  value: string;
  kind?: "accent" | "patched";
};

export type Content = {
  metaTitle: string;
  metaDescription: string;
  ogTitle: string;
  ogDescription: string;
  skipToContent: string;
  brandHome: string;
  brandTagline: string;
  primaryNavigation: string;
  navHardware: string;
  navEngineering: string;
  navPlatform: string;
  navFuture: string;
  packages: string;
  languageSelector: string;
  selectEnglish: string;
  selectRussian: string;
  heroEyebrow: string;
  heroTitleLead: string;
  heroTitleAccent: string;
  heroDescription: string;
  exploreHardware: string;
  openPackages: string;
  heroProofOne: string;
  heroProofTwo: string;
  heroProofThree: string;
  wifiTagLabel: string;
  wifiTagValue: string;
  tierTagLabel: string;
  tierTagValue: string;
  terminalTitle: string;
  terminalCommand: string;
  terminalLines: VerificationLine[];
  terminalReady: string;
  supportEyebrow: string;
  supportTitle: string;
  supportDescription: string;
  platinumTitle: string;
  platinumDescription: string;
  goldTitle: string;
  goldDescription: string;
  boards: Board[];
  engineeringEyebrow: string;
  engineeringTitle: string;
  engineeringDescription: string;
  engineeringQuote: string;
  engineeringStatusLabel: string;
  engineeringTerminalTitle: string;
  engineeringCommand: string;
  engineeringLines: VerificationLine[];
  matrixComplete: string;
  platformEyebrow: string;
  platformTitle: string;
  platformDescription: string;
  features: Feature[];
  platformCommand: string;
  platformOutput: string;
  futureEyebrow: string;
  futureTitle: string;
  futureDescription: string;
  roadmapTerminalTitle: string;
  roadmapCommand: string;
  roadmapLines: VerificationLine[];
  roadmapNote: string;
  ctaEyebrow: string;
  ctaTitle: string;
  ctaDescription: string;
  ctaPrimary: string;
  ctaSecondary: string;
  footerDescription: string;
  footerBuiltBy: string;
};

const english: Content = {
  metaTitle: "1T Lumina — Linux, tuned for your board",
  metaDescription: "1T Lumina is hardware-first Linux for ARM single-board computers.",
  ogTitle: "1T Lumina — Linux, tuned for your board",
  ogDescription: "Platinum-tier support for Orange Pi 5 Ultra, Jetson Orin Nano, and Orange Pi Zero 3.",
  skipToContent: "Skip to main content",
  brandHome: "1T Lumina home",
  brandTagline: "Linux for SBCs",
  primaryNavigation: "Primary navigation",
  navHardware: "Hardware",
  navEngineering: "Engineering",
  navPlatform: "Platform",
  navFuture: "What’s next",
  packages: "Packages",
  languageSelector: "Page language",
  selectEnglish: "Show page in English",
  selectRussian: "Show page in Russian",
  heroEyebrow: "Hardware-first Linux",
  heroTitleLead: "Linux that knows",
  heroTitleAccent: "your board.",
  heroDescription: "1T Lumina is a modern Linux platform for ARM single-board computers. We integrate the difficult hardware, test the complete system, and ship an experience you can actually rely on.",
  exploreHardware: "Explore supported boards",
  openPackages: "Open package network",
  heroProofOne: "Board-specific engineering",
  heroProofTwo: "Complete hardware validation",
  heroProofThree: "Signed native packages",
  wifiTagLabel: "Wi-Fi",
  wifiTagValue: "patched",
  tierTagLabel: "tier",
  tierTagValue: "Platinum",
  terminalTitle: "lumina / hardware probe",
  terminalCommand: "lumina inspect --board orange-pi-5-ultra --all",
  terminalLines: [
    { label: "board", value: "Orange Pi 5 Ultra" },
    { label: "support tier", value: "PLATINUM", kind: "accent" },
    { label: "Wi-Fi driver", value: "PATCHED", kind: "patched" },
    { label: "component matrix", value: "VERIFIED" },
    { label: "system state", value: "READY" }
  ],
  terminalReady: "All checks passed in 2.8s",
  supportEyebrow: "Hardware support",
  supportTitle: "A support tier should mean something.",
  supportDescription: "We publish the level of integration each board has actually earned. Platinum means board-specific engineering and exhaustive validation. Gold means a dependable core with coverage still expanding.",
  platinumTitle: "Platinum tier",
  platinumDescription: "Deep platform integration. The full hardware path is validated as one system.",
  goldTitle: "Gold tier",
  goldDescription: "A stable core experience with board-specific coverage continuing to grow.",
  boards: [
    {
      name: "Orange Pi 5 Ultra",
      tier: "platinum",
      tierLabel: "Platinum",
      useCase: "Desktop · development · edge",
      description: "Our flagship Rockchip platform. We patched the Wi-Fi driver ourselves, checked every component, and verified the complete board experience.",
      facts: ["Patched Wi-Fi", "Full validation", "RK3588"]
    },
    {
      name: "NVIDIA Jetson Orin Nano",
      tier: "platinum",
      tierLabel: "Platinum",
      useCase: "AI · robotics · accelerated edge",
      description: "A version-matched NVIDIA platform with integrated kernel, firmware, graphics, multimedia, CUDA, and a hardware-accepted desktop image.",
      facts: ["NVIDIA stack", "CUDA", "Desktop ready"]
    },
    {
      name: "Orange Pi Zero 3",
      tier: "platinum",
      tierLabel: "Platinum",
      useCase: "Compact services · embedded · lab",
      description: "Platinum board-level support in a tiny, efficient ARM64 footprint for projects that need more platform than board.",
      facts: ["ARM64", "Compact", "Board-tuned"]
    },
    {
      name: "Orange Pi 5 Plus",
      tier: "gold",
      tierLabel: "Gold",
      useCase: "Desktop · server · maker",
      description: "A dependable core Lumina experience today, with the remaining board-specific validation continuing toward Platinum.",
      facts: ["Stable core", "RK3588", "Coverage growing"]
    }
  ],
  engineeringEyebrow: "Case file 001 · Orange Pi 5 Ultra",
  engineeringTitle: "We didn’t wait for Wi-Fi to fix itself.",
  engineeringDescription: "Hardware support is more than a successful boot. When the wireless stack needed work, we patched the driver. Then we went back through the board component by component until the complete matrix passed.",
  engineeringQuote: "Platinum is the result of the work, not a marketing label.",
  engineeringStatusLabel: "PLATINUM / QA",
  engineeringTerminalTitle: "orange-pi-5-ultra / validation",
  engineeringCommand: "lumina verify --profile platinum --verbose",
  engineeringLines: [
    { label: "board identity", value: "PASS" },
    { label: "boot chain", value: "PASS" },
    { label: "Wi-Fi driver", value: "PATCHED + PASS", kind: "patched" },
    { label: "display & graphics", value: "PASS" },
    { label: "audio", value: "PASS" },
    { label: "storage & I/O", value: "PASS" },
    { label: "thermal & power", value: "PASS" }
  ],
  matrixComplete: "Component matrix complete",
  platformEyebrow: "The Lumina approach",
  platformTitle: "The terminal is modern. The engineering goes deeper.",
  platformDescription: "Lumina brings kernel, drivers, firmware, desktop, packaging, and release infrastructure together as one maintained platform.",
  features: [
    {
      index: "01",
      title: "Board-aware integration",
      description: "Kernel, firmware, device configuration, and vendor components are matched to the hardware instead of assembled as an afterthought."
    },
    {
      index: "02",
      title: "Whole-system validation",
      description: "We test the paths people use: boot, network, display, audio, storage, acceleration, thermals, and updates."
    },
    {
      index: "03",
      title: "A trustworthy supply chain",
      description: "Native builds, signed RPMs, and atomic publishing connect engineering work to the systems running it."
    }
  ],
  platformCommand: "sudo dnf repolist --enabled | grep lumina",
  platformOutput: "lumina-lumen   1T Lumina signed packages",
  futureEyebrow: "Beyond the current matrix",
  futureTitle: "More boards are entering the lab.",
  futureDescription: "We are expanding Lumina across the SBC ecosystem. New boards move from bring-up to validation and earn a public tier only when the real hardware is ready.",
  roadmapTerminalTitle: "lumina / board qualification queue",
  roadmapCommand: "lumina roadmap --watch",
  roadmapLines: [
    { label: "community signals", value: "LISTENING" },
    { label: "board acquisition", value: "IN PROGRESS", kind: "accent" },
    { label: "platform bring-up", value: "QUEUED" },
    { label: "validation matrix", value: "REQUIRED" },
    { label: "public tier", value: "WHEN READY" }
  ],
  roadmapNote: "No paper launches. Hardware earns support in the lab.",
  ctaEyebrow: "Start with Lumina",
  ctaTitle: "Your board can feel like a complete computer.",
  ctaDescription: "Explore the signed package network today. Installation images and expanded board support are on the way.",
  ctaPrimary: "Browse Lumina packages",
  ctaSecondary: "See the build platform",
  footerDescription: "Hardware-first Linux for ARM single-board computers.",
  footerBuiltBy: "Engineered and verified by LuminaCI"
};

const russian: Content = {
  metaTitle: "1T Lumina — Linux, настроенный для вашей платы",
  metaDescription: "1T Lumina — аппаратно-ориентированный Linux для одноплатных ARM-компьютеров.",
  ogTitle: "1T Lumina — Linux, настроенный для вашей платы",
  ogDescription: "Поддержка уровня Platinum для Orange Pi 5 Ultra, Jetson Orin Nano и Orange Pi Zero 3.",
  skipToContent: "Перейти к основному содержимому",
  brandHome: "Главная страница 1T Lumina",
  brandTagline: "Linux для одноплатников",
  primaryNavigation: "Основная навигация",
  navHardware: "Устройства",
  navEngineering: "Инженерия",
  navPlatform: "Платформа",
  navFuture: "Что дальше",
  packages: "Пакеты",
  languageSelector: "Язык страницы",
  selectEnglish: "Показать страницу на английском языке",
  selectRussian: "Показать страницу на русском языке",
  heroEyebrow: "Linux начинается с железа",
  heroTitleLead: "Linux, который знает",
  heroTitleAccent: "вашу плату.",
  heroDescription: "1T Lumina — современная Linux-платформа для одноплатных ARM-компьютеров. Мы интегрируем сложное оборудование, проверяем систему целиком и выпускаем решение, на которое можно положиться.",
  exploreHardware: "Поддерживаемые устройства",
  openPackages: "Открыть сеть пакетов",
  heroProofOne: "Инженерия для каждой платы",
  heroProofTwo: "Полная проверка оборудования",
  heroProofThree: "Подписанные нативные пакеты",
  wifiTagLabel: "Wi-Fi",
  wifiTagValue: "исправлен",
  tierTagLabel: "уровень",
  tierTagValue: "Platinum",
  terminalTitle: "lumina / проверка оборудования",
  terminalCommand: "lumina inspect --board orange-pi-5-ultra --all",
  terminalLines: [
    { label: "плата", value: "Orange Pi 5 Ultra" },
    { label: "уровень поддержки", value: "PLATINUM", kind: "accent" },
    { label: "драйвер Wi-Fi", value: "ИСПРАВЛЕН", kind: "patched" },
    { label: "матрица компонентов", value: "ПРОВЕРЕНА" },
    { label: "состояние системы", value: "ГОТОВА" }
  ],
  terminalReady: "Все проверки пройдены за 2,8 с",
  supportEyebrow: "Поддержка оборудования",
  supportTitle: "Уровень поддержки должен что-то значить.",
  supportDescription: "Мы публикуем тот уровень интеграции, который устройство подтвердило на практике. Platinum означает отдельную инженерную работу и исчерпывающую проверку. Gold — надёжную основу с растущим покрытием.",
  platinumTitle: "Уровень Platinum",
  platinumDescription: "Глубокая интеграция платформы. Весь аппаратный тракт проверен как единая система.",
  goldTitle: "Уровень Gold",
  goldDescription: "Стабильная основа с продолжающейся проверкой специфичных для платы компонентов.",
  boards: [
    {
      name: "Orange Pi 5 Ultra",
      tier: "platinum",
      tierLabel: "Platinum",
      useCase: "Рабочая станция · разработка · edge",
      description: "Наша флагманская платформа Rockchip. Мы сами исправили драйвер Wi-Fi, проверили каждый компонент и подтвердили работу всей платы.",
      facts: ["Wi-Fi исправлен", "Полная проверка", "RK3588"]
    },
    {
      name: "NVIDIA Jetson Orin Nano",
      tier: "platinum",
      tierLabel: "Platinum",
      useCase: "ИИ · робототехника · edge-ускорение",
      description: "Согласованная платформа NVIDIA: ядро, прошивки, графика, мультимедиа, CUDA и образ рабочего стола, проверенный на реальном устройстве.",
      facts: ["Стек NVIDIA", "CUDA", "Рабочий стол"]
    },
    {
      name: "Orange Pi Zero 3",
      tier: "platinum",
      tierLabel: "Platinum",
      useCase: "Компактные сервисы · embedded · лаборатория",
      description: "Поддержка платы уровня Platinum в компактной и эффективной ARM64-системе для проектов, где особенно важен малый формат.",
      facts: ["ARM64", "Компактность", "Настроено для платы"]
    },
    {
      name: "Orange Pi 5 Plus",
      tier: "gold",
      tierLabel: "Gold",
      useCase: "Рабочая станция · сервер · maker",
      description: "Надёжная основа Lumina уже сегодня. Проверка остальных специфичных компонентов продолжается на пути к Platinum.",
      facts: ["Стабильная основа", "RK3588", "Покрытие растёт"]
    }
  ],
  engineeringEyebrow: "Досье 001 · Orange Pi 5 Ultra",
  engineeringTitle: "Мы не ждали, пока Wi-Fi исправится сам.",
  engineeringDescription: "Поддержка оборудования — это больше, чем успешная загрузка. Когда беспроводной стек потребовал доработки, мы исправили драйвер. Затем последовательно проверили каждый компонент, пока вся матрица не стала зелёной.",
  engineeringQuote: "Platinum — результат проделанной работы, а не маркетинговая метка.",
  engineeringStatusLabel: "PLATINUM / ПРОВЕРЕНО",
  engineeringTerminalTitle: "orange-pi-5-ultra / проверка",
  engineeringCommand: "lumina verify --profile platinum --verbose",
  engineeringLines: [
    { label: "идентификация платы", value: "ПРОЙДЕНО" },
    { label: "цепочка загрузки", value: "ПРОЙДЕНО" },
    { label: "драйвер Wi-Fi", value: "ИСПРАВЛЕН + ПРОЙДЕНО", kind: "patched" },
    { label: "дисплей и графика", value: "ПРОЙДЕНО" },
    { label: "аудио", value: "ПРОЙДЕНО" },
    { label: "накопители и I/O", value: "ПРОЙДЕНО" },
    { label: "температуры и питание", value: "ПРОЙДЕНО" }
  ],
  matrixComplete: "Матрица компонентов заполнена",
  platformEyebrow: "Подход Lumina",
  platformTitle: "Терминал современный. Инженерия — ещё глубже.",
  platformDescription: "Lumina объединяет ядро, драйверы, прошивки, рабочий стол, пакеты и инфраструктуру релизов в одну поддерживаемую платформу.",
  features: [
    {
      index: "01",
      title: "Интеграция с учётом платы",
      description: "Ядро, прошивки, конфигурация устройств и компоненты производителя согласованы с оборудованием, а не собраны постфактум."
    },
    {
      index: "02",
      title: "Проверка системы целиком",
      description: "Мы тестируем реальные сценарии: загрузку, сеть, дисплей, звук, накопители, ускорение, температуры и обновления."
    },
    {
      index: "03",
      title: "Надёжная цепочка поставки",
      description: "Нативные сборки, подписанные RPM и атомарная публикация связывают инженерную работу с работающими системами."
    }
  ],
  platformCommand: "sudo dnf repolist --enabled | grep lumina",
  platformOutput: "lumina-lumen   подписанные пакеты 1T Lumina",
  futureEyebrow: "За пределами текущей матрицы",
  futureTitle: "Новые платы уже прибывают в лабораторию.",
  futureDescription: "Мы расширяем Lumina на экосистему одноплатных компьютеров. Новые устройства проходят запуск и проверку и получают публичный уровень только тогда, когда реальное оборудование готово.",
  roadmapTerminalTitle: "lumina / очередь проверки плат",
  roadmapCommand: "lumina roadmap --watch",
  roadmapLines: [
    { label: "запросы сообщества", value: "СЛУШАЕМ" },
    { label: "получение плат", value: "В ПРОЦЕССЕ", kind: "accent" },
    { label: "запуск платформы", value: "В ОЧЕРЕДИ" },
    { label: "матрица проверок", value: "ОБЯЗАТЕЛЬНА" },
    { label: "публичный уровень", value: "ПО ГОТОВНОСТИ" }
  ],
  roadmapNote: "Никаких бумажных релизов. Поддержка подтверждается в лаборатории.",
  ctaEyebrow: "Начните с Lumina",
  ctaTitle: "Ваша плата может стать полноценным компьютером.",
  ctaDescription: "Уже сейчас доступна сеть подписанных пакетов. Установочные образы и поддержка новых плат — на подходе.",
  ctaPrimary: "Открыть пакеты Lumina",
  ctaSecondary: "Посмотреть платформу сборки",
  footerDescription: "Аппаратно-ориентированный Linux для одноплатных ARM-компьютеров.",
  footerBuiltBy: "Разработано и проверено LuminaCI"
};

export const content: Record<Language, Content> = {
  en: english,
  ru: russian
};

export function resolveLanguage(saved: string | null, browserLanguages: readonly string[]): Language {
  if (saved === "en" || saved === "ru") return saved;
  return browserLanguages.some(language => language.toLocaleLowerCase().startsWith("ru")) ? "ru" : "en";
}
