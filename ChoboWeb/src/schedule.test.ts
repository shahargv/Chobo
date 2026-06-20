import { describe, expect, it } from "vitest";
import { buildCron, defaultScheduleDraft, parseCronToDraft, summarizeSchedule } from "./schedule";

describe("schedule presets", () => {
  it("builds daily at 3AM without requiring raw cron", () => {
    expect(buildCron({ ...defaultScheduleDraft, preset: "daily", hour: 3, minute: 0 })).toBe("0 0 3 * * ?");
    expect(summarizeSchedule({ ...defaultScheduleDraft, preset: "daily", hour: 3 }, "UTC")).toBe("Daily at 03:00 (UTC)");
  });

  it("builds selected Sunday and Thursday schedules", () => {
    const draft = { ...defaultScheduleDraft, preset: "selected-days" as const, selectedDays: ["SUN", "THU"], hour: 4, minute: 15 };
    expect(buildCron(draft)).toBe("0 15 4 ? * SUN,THU");
    expect(parseCronToDraft("0 15 4 ? * SUN,THU").preset).toBe("selected-days");
  });

  it("round-trips every-hours schedules before daily parsing", () => {
    const draft = { ...defaultScheduleDraft, preset: "every-hours" as const, everyHours: 6, minute: 5 };
    expect(buildCron(draft)).toBe("0 5 0/6 * * ?");
    expect(parseCronToDraft("0 5 0/6 * * ?").preset).toBe("every-hours");
  });
});
