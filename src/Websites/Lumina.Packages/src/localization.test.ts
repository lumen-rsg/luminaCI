import { describe, expect, it } from "vitest";
import { formatPackageCount, resolveLanguage } from "./localization";

describe("localization", () => {
  it("honors a saved language before the browser preference", () => {
    expect(resolveLanguage("en", ["ru-RU"])).toBe("en");
    expect(resolveLanguage("ru", ["en-US"])).toBe("ru");
  });

  it("selects Russian for a Russian browser locale", () => {
    expect(resolveLanguage(null, ["en-US", "ru-RU"])).toBe("ru");
    expect(resolveLanguage(null, ["en-US"])).toBe("en");
  });

  it("uses the correct package-count form", () => {
    expect(formatPackageCount(1, "en")).toBe("1 package");
    expect(formatPackageCount(2, "en")).toBe("2 packages");
    expect(formatPackageCount(1, "ru")).toBe("1 пакет");
    expect(formatPackageCount(2, "ru")).toBe("2 пакета");
    expect(formatPackageCount(5, "ru")).toBe("5 пакетов");
    expect(formatPackageCount(21, "ru")).toBe("21 пакет");
  });
});
