import { describe, expect, it } from "vitest";
import { visibleProfiles, emptyProfile } from "../src/lib/api";

describe("provider list", () => {
  const rows = [
    {
      ...emptyProfile(),
      id: "a",
      name: "自定义备用",
      baseUrl: "https://backup.example",
      apiKey: "secret-never-search",
      model: "gpt-example",
    },
    {
      ...emptyProfile(),
      id: "b",
      name: "日常开发",
      baseUrl: "https://primary.example",
      notes: "工作配置",
    },
  ];
  it("searches names, addresses, models and notes without searching secrets", () => {
    expect(
      visibleProfiles(rows, " GPT-EXAMPLE ", null, "active").map((p) => p.id),
    ).toEqual(["a"]);
    expect(
      visibleProfiles(rows, "工作", null, "active").map((p) => p.id),
    ).toEqual(["b"]);
    expect(
      visibleProfiles(rows, "secret-never-search", null, "active"),
    ).toEqual([]);
  });
  it("puts the live configuration first without changing stored order", () => {
    expect(visibleProfiles(rows, "", "b", "active").map((p) => p.id)).toEqual([
      "b",
      "a",
    ]);
    expect(rows.map((p) => p.id)).toEqual(["a", "b"]);
  });
});
