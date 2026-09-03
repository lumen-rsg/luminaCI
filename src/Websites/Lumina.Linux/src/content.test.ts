import { describe, expect, it } from "vitest";
import { content, resolveLanguage } from "./content";

describe("site content", () => {
  it("honors a saved language before browser preferences", () => {
    expect(resolveLanguage("en", ["ru-RU"])).toBe("en");
    expect(resolveLanguage("ru", ["en-US"])).toBe("ru");
  });

  it("selects Russian for a Russian browser locale", () => {
    expect(resolveLanguage(null, ["en-US", "ru-RU"])).toBe("ru");
    expect(resolveLanguage(null, ["en-US"])).toBe("en");
  });

  it("keeps support tiers aligned across languages", () => {
    expect(content.en.boards.map(board => board.tier)).toEqual(["platinum", "platinum", "platinum", "gold"]);
    expect(content.ru.boards.map(board => board.tier)).toEqual(content.en.boards.map(board => board.tier));
  });

  it("provides localized terminal chrome", () => {
    expect(content.en.engineeringTerminalTitle).not.toBe(content.ru.engineeringTerminalTitle);
    expect(content.en.roadmapTerminalTitle).not.toBe(content.ru.roadmapTerminalTitle);
    expect(content.ru.wifiTagValue).toBe("исправлен");
  });
});
