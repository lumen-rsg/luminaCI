import {
  ArrowRight,
  ArrowUpRight,
  Boxes,
  Check,
  CircuitBoard,
  Command,
  Cpu,
  PackageCheck,
  Radio,
  ShieldCheck,
  Sparkles,
  TerminalSquare,
  Wifi
} from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import {
  content,
  resolveLanguage,
  type Content,
  type Language,
  type SupportTier,
  type VerificationLine
} from "./content";

function preferredLanguage(): Language {
  const browserLanguages = navigator.languages.length > 0 ? navigator.languages : [navigator.language];
  return resolveLanguage(window.localStorage.getItem("lumina-linux-language"), browserLanguages);
}

function Brand({ copy }: { copy: Content }) {
  return (
    <a className="brand" href="#top" aria-label={copy.brandHome}>
      <span className="brand__mark"><span>1T</span><i /></span>
      <span className="brand__name"><strong>Lumina</strong><small>{copy.brandTagline}</small></span>
    </a>
  );
}

function LanguageSwitcher({ language, copy, onChange }: {
  language: Language;
  copy: Content;
  onChange: (language: Language) => void;
}) {
  return (
    <div className="language-switcher" role="group" aria-label={copy.languageSelector}>
      <button type="button" lang="en" aria-label={copy.selectEnglish} aria-pressed={language === "en"} onClick={() => onChange("en")}>EN</button>
      <button type="button" lang="ru" aria-label={copy.selectRussian} aria-pressed={language === "ru"} onClick={() => onChange("ru")}>RU</button>
    </div>
  );
}

function TerminalWindow({ title, command, lines, footer, className = "" }: {
  title: string;
  command: string;
  lines: VerificationLine[];
  footer?: string;
  className?: string;
}) {
  return (
    <div className={`terminal-window ${className}`.trim()}>
      <header className="terminal-window__bar">
        <span className="window-controls" aria-hidden="true"><i /><i /><i /></span>
        <span className="terminal-window__title"><TerminalSquare aria-hidden="true" />{title}</span>
        <span className="terminal-window__signal" aria-hidden="true" />
      </header>
      <div className="terminal-window__body">
        <div className="terminal-command"><span>$</span><code>{command}</code></div>
        <div className="terminal-output">
          {lines.map(line => (
            <div className="terminal-output__line" key={line.label}>
              <span className="terminal-output__key">{line.label}</span>
              <span className="terminal-output__dots" aria-hidden="true" />
              <strong data-kind={line.kind ?? "pass"}>{line.value}</strong>
            </div>
          ))}
        </div>
        {footer && <div className="terminal-complete"><Check aria-hidden="true" /><span>{footer}</span><i aria-hidden="true" /></div>}
      </div>
    </div>
  );
}

function TierBadge({ tier, children }: { tier: SupportTier; children: ReactNode }) {
  return <span className="tier-badge" data-tier={tier}><span aria-hidden="true" />{children}</span>;
}

function BoardIcon({ name }: { name: string }) {
  if (name.includes("Jetson")) return <Cpu aria-hidden="true" />;
  if (name.includes("Zero")) return <Radio aria-hidden="true" />;
  return <CircuitBoard aria-hidden="true" />;
}

export default function App() {
  const [language, setLanguage] = useState<Language>(preferredLanguage);
  const copy = content[language];

  useEffect(() => {
    document.documentElement.lang = language;
    document.title = copy.metaTitle;
    document.querySelector('meta[name="description"]')?.setAttribute("content", copy.metaDescription);
    document.querySelector('meta[property="og:title"]')?.setAttribute("content", copy.ogTitle);
    document.querySelector('meta[property="og:description"]')?.setAttribute("content", copy.ogDescription);
    window.localStorage.setItem("lumina-linux-language", language);
  }, [copy, language]);

  return (
    <>
      <a className="skip-link" href="#main-content">{copy.skipToContent}</a>
      <header className="site-header" id="top">
        <div className="header-shell">
          <Brand copy={copy} />
          <nav aria-label={copy.primaryNavigation}>
            <a href="#hardware">{copy.navHardware}</a>
            <a href="#engineering">{copy.navEngineering}</a>
            <a href="#platform">{copy.navPlatform}</a>
            <a href="#future">{copy.navFuture}</a>
          </nav>
          <div className="header-actions">
            <a className="packages-link" href="https://packages.lumina.1t.ru">{copy.packages}<ArrowUpRight aria-hidden="true" /></a>
            <LanguageSwitcher language={language} copy={copy} onChange={setLanguage} />
          </div>
        </div>
      </header>

      <main id="main-content">
        <section className="hero section-shell" aria-labelledby="hero-title">
          <div className="hero-grid" aria-hidden="true" />
          <div className="hero-glow hero-glow--one" aria-hidden="true" />
          <div className="hero-glow hero-glow--two" aria-hidden="true" />
          <div className="hero-copy">
            <span className="eyebrow"><Sparkles aria-hidden="true" />{copy.heroEyebrow}</span>
            <h1 id="hero-title">{copy.heroTitleLead}<br /><em>{copy.heroTitleAccent}</em></h1>
            <p>{copy.heroDescription}</p>
            <div className="hero-actions">
              <a className="button button--primary" href="#hardware">{copy.exploreHardware}<ArrowRight aria-hidden="true" /></a>
              <a className="button button--quiet" href="https://packages.lumina.1t.ru">{copy.openPackages}<PackageCheck aria-hidden="true" /></a>
            </div>
            <div className="proof-row">
              <span><CircuitBoard aria-hidden="true" />{copy.heroProofOne}</span>
              <span><Check aria-hidden="true" />{copy.heroProofTwo}</span>
              <span><ShieldCheck aria-hidden="true" />{copy.heroProofThree}</span>
            </div>
          </div>
          <div className="hero-terminal-wrap">
            <div className="terminal-aura" aria-hidden="true" />
            <div className="terminal-tag terminal-tag--top"><Wifi aria-hidden="true" /><span><small>{copy.wifiTagLabel}</small><strong>{copy.wifiTagValue}</strong></span></div>
            <div className="terminal-tag terminal-tag--bottom"><ShieldCheck aria-hidden="true" /><span><small>{copy.tierTagLabel}</small><strong>{copy.tierTagValue}</strong></span></div>
            <TerminalWindow
              title={copy.terminalTitle}
              command={copy.terminalCommand}
              lines={copy.terminalLines}
              footer={copy.terminalReady}
              className="terminal-window--hero"
            />
          </div>
        </section>

        <section className="support section-shell" id="hardware" aria-labelledby="support-title">
          <div className="section-intro">
            <span className="eyebrow"><CircuitBoard aria-hidden="true" />{copy.supportEyebrow}</span>
            <div className="section-intro__row">
              <h2 id="support-title">{copy.supportTitle}</h2>
              <p>{copy.supportDescription}</p>
            </div>
          </div>
          <div className="tier-legend" aria-label={copy.supportEyebrow}>
            <article data-tier="platinum"><span className="tier-legend__mark"><Sparkles aria-hidden="true" /></span><div><strong>{copy.platinumTitle}</strong><p>{copy.platinumDescription}</p></div></article>
            <article data-tier="gold"><span className="tier-legend__mark"><ShieldCheck aria-hidden="true" /></span><div><strong>{copy.goldTitle}</strong><p>{copy.goldDescription}</p></div></article>
          </div>
          <div className="board-grid">
            {copy.boards.map((board, index) => (
              <article className="board-card" data-tier={board.tier} key={board.name}>
                <header>
                  <span className="board-card__icon"><BoardIcon name={board.name} /></span>
                  <TierBadge tier={board.tier}>{board.tierLabel}</TierBadge>
                </header>
                <span className="board-card__index">0{index + 1}</span>
                <h3>{board.name}</h3>
                <small>{board.useCase}</small>
                <p>{board.description}</p>
                <footer>{board.facts.map(fact => <span key={fact}>{fact}</span>)}</footer>
              </article>
            ))}
          </div>
        </section>

        <section className="engineering" id="engineering" aria-labelledby="engineering-title">
          <div className="section-shell engineering-shell">
            <div className="engineering-copy">
              <span className="eyebrow"><Wifi aria-hidden="true" />{copy.engineeringEyebrow}</span>
              <h2 id="engineering-title">{copy.engineeringTitle}</h2>
              <p>{copy.engineeringDescription}</p>
              <blockquote>{copy.engineeringQuote}</blockquote>
            </div>
            <div className="verification-panel">
              <span className="verification-panel__label"><span />{copy.engineeringStatusLabel}</span>
              <TerminalWindow
                title={copy.engineeringTerminalTitle}
                command={copy.engineeringCommand}
                lines={copy.engineeringLines}
                footer={copy.matrixComplete}
              />
            </div>
          </div>
        </section>

        <section className="platform section-shell" id="platform" aria-labelledby="platform-title">
          <div className="section-intro section-intro--centered">
            <span className="eyebrow"><Command aria-hidden="true" />{copy.platformEyebrow}</span>
            <h2 id="platform-title">{copy.platformTitle}</h2>
            <p>{copy.platformDescription}</p>
          </div>
          <div className="feature-list">
            {copy.features.map(feature => (
              <article key={feature.index}>
                <span>{feature.index}</span>
                <div><h3>{feature.title}</h3><p>{feature.description}</p></div>
                <ArrowUpRight aria-hidden="true" />
              </article>
            ))}
          </div>
          <div className="command-strip">
            <span className="command-strip__prompt">$</span>
            <code>{copy.platformCommand}</code>
            <span className="command-strip__result"><Check aria-hidden="true" />{copy.platformOutput}</span>
          </div>
        </section>

        <section className="future section-shell" id="future" aria-labelledby="future-title">
          <div className="future-copy">
            <span className="eyebrow"><Boxes aria-hidden="true" />{copy.futureEyebrow}</span>
            <h2 id="future-title">{copy.futureTitle}</h2>
            <p>{copy.futureDescription}</p>
            <div className="future-note"><ShieldCheck aria-hidden="true" />{copy.roadmapNote}</div>
          </div>
          <TerminalWindow
            title={copy.roadmapTerminalTitle}
            command={copy.roadmapCommand}
            lines={copy.roadmapLines}
            className="terminal-window--roadmap"
          />
        </section>

        <section className="cta section-shell" aria-labelledby="cta-title">
          <div className="cta-mark" aria-hidden="true"><TerminalSquare /><span>1T</span></div>
          <div className="cta-copy">
            <span className="eyebrow">{copy.ctaEyebrow}</span>
            <h2 id="cta-title">{copy.ctaTitle}</h2>
            <p>{copy.ctaDescription}</p>
          </div>
          <div className="cta-actions">
            <a className="button button--light" href="https://packages.lumina.1t.ru">{copy.ctaPrimary}<ArrowRight aria-hidden="true" /></a>
            <a className="button button--outline" href="https://console.lumina.1t.ru">{copy.ctaSecondary}<ArrowUpRight aria-hidden="true" /></a>
          </div>
        </section>
      </main>

      <footer className="site-footer">
        <div className="section-shell">
          <Brand copy={copy} />
          <p>{copy.footerDescription}</p>
          <span>{copy.footerBuiltBy}</span>
        </div>
      </footer>
    </>
  );
}
