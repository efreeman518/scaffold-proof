import { test, expect } from "@playwright/test";
import {
  clickDeleteOnRow,
  clickEditOnRow,
  clickSave,
  clickSaveNewTask,
  confirmDeleteDialog,
  expectSnackbar,
  expectTaskInTable,
  expectTaskNotInTable,
  fillTextField,
  navigateToNewTask,
  navigateToTaskList,
  searchForTask,
  selectOption,
  uniqueTitle,
  waitForApp,
} from "../../utils/blazorTestUtils";

/**
 * Full Task CRUD lifecycle exercised through the Blazor MudBlazor UI.
 *
 * Prerequisites:
 *   1. PLAYWRIGHT_BLAZOR_URL points at the hosted Blazor app
 *   2. API running with seed data (Aspire AppHost or standalone)
 *   3. `npx playwright install --with-deps chromium`
 *
 * Run:  npm run test:blazor
 */

test.describe("TaskFlow Blazor - Task CRUD lifecycle", () => {
  // Tests must run in order - each depends on the previous test's side effects
  test.describe.configure({ mode: "serial" });
  let taskTitle: string;
  let updatedTitle: string;
  let checklistTitle: string;
  let commentBody: string;

  test.beforeAll(() => {
    taskTitle = uniqueTitle("E2E-Create");
    updatedTitle = uniqueTitle("E2E-Updated");
    checklistTitle = uniqueTitle("E2E-Checklist");
    commentBody = uniqueTitle("E2E-Comment");
  });

  test.beforeEach(async ({ page }) => {
    page.setDefaultTimeout(30_000);
  });

  // -- CREATE ----------------------------------------------------------
  test("1. create a new task", async ({ page }) => {
    await waitForApp(page);
    await navigateToNewTask(page);

    await fillTextField(page, "Title", taskTitle);
    await fillTextField(page, "Description", "Automated Playwright E2E test task");
    await page.getByText(/^Checklist \(0\)$/).click();
    await page.getByPlaceholder("Add item...").fill(checklistTitle);
    await page.getByPlaceholder("Add item...").press("Enter");

    await page.getByText(/^Comments \(0\)$/).click();
    await page.getByPlaceholder("Add a comment...").fill(commentBody);
    await page.getByRole("button", { name: /^add$/i }).last().click();
    // Status=Open and Priority=Medium are defaults - no need to select them

    await clickSaveNewTask(page);

    await navigateToTaskList(page);
    await searchForTask(page, taskTitle);
    await expectTaskInTable(page, taskTitle, 20_000);
  });

  // -- READ ------------------------------------------------------------
  test("2. read the created task in the list", async ({ page }) => {
    await navigateToTaskList(page);
    await searchForTask(page, taskTitle);
    await expectTaskInTable(page, taskTitle);

    // Verify row contains expected metadata
    const row = page.locator(`.mud-table-body tr:has-text("${taskTitle}")`);
    await expect(row).toContainText("Open");
    await expect(row).toContainText("Medium");

    await clickEditOnRow(page, taskTitle);
    await expect(page.getByText(checklistTitle)).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText(commentBody)).toBeVisible({ timeout: 10_000 });

    // Navigate back to list so test 3 starts from a clean state (avoids
    // the Blazor navigation guard on the still-open edit form).
    await navigateToTaskList(page);
  });

  // -- UPDATE ----------------------------------------------------------
  test("3. update the task title and priority", async ({ page }) => {
    await navigateToTaskList(page);
    await searchForTask(page, taskTitle);
    await clickEditOnRow(page, taskTitle);

    // Clear and re-fill the title
    await fillTextField(page, "Title", updatedTitle);
    await selectOption(page, "Priority", "High");

    await clickSave(page);

    await navigateToTaskList(page);
    await searchForTask(page, updatedTitle);
    await expectTaskInTable(page, updatedTitle);
    await expectTaskNotInTable(page, taskTitle);

    const row = page.locator(`.mud-table-body tr:has-text("${updatedTitle}")`);
    await expect(row).toContainText("High");
  });

  // -- DELETE ----------------------------------------------------------
  test("4. delete the task", async ({ page }) => {
    await navigateToTaskList(page);
    await searchForTask(page, updatedTitle);
    await clickDeleteOnRow(page, updatedTitle);
    await confirmDeleteDialog(page);

    // Task should vanish from the table
    await expectTaskNotInTable(page, updatedTitle);
  });
});

test.describe("TaskFlow Blazor - two-tab optimistic concurrency (412)", () => {
  test("editing the same task from two tabs surfaces a 412 on the second save", async ({ page, context }) => {
    const title = uniqueTitle("E2E-Conflict");

    // Tab A creates the task and stays on its edit page (holds the just-loaded Version/ETag).
    await waitForApp(page);
    await navigateToNewTask(page);
    await fillTextField(page, "Title", title);
    await clickSaveNewTask(page);
    const taskUrl = page.url();

    // Tab B loads the same task independently, capturing the same If-Match currency as tab A.
    const pageB = await context.newPage();
    await pageB.goto(taskUrl, { waitUntil: "networkidle" });
    await expect(pageB.getByRole("heading", { name: /edit task/i })).toBeVisible({ timeout: 15_000 });

    // Tab A saves first: succeeds and bumps the server-side Version past what tab B is holding.
    await selectOption(page, "Priority", "High");
    await clickSave(page);
    await expect(page).toHaveURL(/\/tasks\/[0-9a-f-]{36}$/i, { timeout: 15_000 });

    // Tab B saves against its now-stale Version: the API returns 412, and the UI reports the
    // conflict and reloads instead of silently overwriting tab A's change.
    await selectOption(pageB, "Priority", "Low");
    await clickSave(pageB);
    await expectSnackbar(pageB, "Task changed elsewhere, reloading.", 15_000);

    await pageB.close();

    // Cleanup.
    await navigateToTaskList(page);
    await searchForTask(page, title);
    await clickDeleteOnRow(page, title);
    await confirmDeleteDialog(page);
    await expectTaskNotInTable(page, title);
  });
});
