import { describe, expect, it } from "vitest";
import { collectPackages, formatBytes, parseRpmFileName } from "./repository";

describe("repository index", () => {
  it("parses a hyphenated aarch64 RPM name from the right", () => {
    expect(parseRpmFileName(
      "kernel-tegra-l4t-39.2.0-3.lu26.aarch64.rpm",
      "aarch64",
      { name: "kernel-tegra-l4t-39.2.0-3.lu26.aarch64.rpm", type: "file", size: 44_653_357 }
    )).toMatchObject({
      architecture: "aarch64",
      name: "kernel-tegra-l4t",
      release: "3.lu26",
      size: 44_653_357,
      version: "39.2.0"
    });
  });

  it("rejects metadata and foreign-architecture files", () => {
    expect(parseRpmFileName("repomd.xml", "aarch64", { name: "repomd.xml", type: "file" })).toBeNull();
    expect(parseRpmFileName(
      "sample-1-1.x86_64.rpm",
      "aarch64",
      { name: "sample-1-1.x86_64.rpm", type: "file" }
    )).toBeNull();
  });

  it("collects only RPM files", () => {
    const packages = collectPackages("noarch", [
      { name: "lumina-release-26.08-3.lu26.noarch.rpm", type: "file", size: 10_000 },
      { name: "repodata", type: "directory" },
      { name: "repomd.xml", type: "file", size: 2_000 }
    ]);
    expect(packages).toHaveLength(1);
    expect(packages[0].name).toBe("lumina-release");
  });

  it("formats package sizes", () => {
    expect(formatBytes(0)).toBe("0 B");
    expect(formatBytes(1_048_576)).toBe("1 MiB");
  });
});
