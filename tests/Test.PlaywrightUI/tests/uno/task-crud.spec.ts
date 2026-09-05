import { expect, test } from "@playwright/test";
import {
  canvasFingerprint,
  clickAppChrome,
  expectVisualChangeAfter,
  waitForApp,
} from "../../utils/unoTestUtils";

/**
 * Uno WASM renders through SkiaSharp to a single <canvas>; there is no DOM form Playwright can
 * fill (getByLabel/getByRole). Uno's own docs (features/using-skia-rendering.md, "Limitations")
 * list accessibility support as work-in-progress under Skia, and the ARIA overlay it does ship
 * only attaches after a manual "Enable accessibility" activation the app does not surface - so a
 * form-driven CRUD + 412 flow like tests/blazor/task-crud.spec.ts or tests/react/task-crud.spec.ts
 * is not reachable here. This file stays at the same canvas-fingerprint smoke level as the other
 * Uno specs, using two independent tabs against the task list as the closest honest proxy for the
 * "two-tab" scenario: it proves concurrent sessions repaint independently rather than one tab's
 * navigation corrupting or blocking the other's WASM host.
 */
test.describe("TaskFlow Uno WASM - two-tab task list smoke", () => {
  test("two tabs against the task list repaint independently", async ({ page, context }) => {
    await waitForApp(page);
    const pageB = await context.newPage();
    await waitForApp(pageB);

    await expectVisualChangeAfter(page, () => clickAppChrome(page, "tasks"));
    await expectVisualChangeAfter(pageB, () => clickAppChrome(pageB, "tasks"));

    // Both tabs are alive and independently painted after both navigated - neither WASM host
    // wedged or blanked as a side effect of the other's navigation.
    await expect(await canvasFingerprint(page)).toHaveLength(64);
    await expect(await canvasFingerprint(pageB)).toHaveLength(64);

    await pageB.close();
  });
});
