import { expect, test, type Page } from "@playwright/test";
import {
  addChecklistItem,
  addComment,
  deleteTaskFromList,
  expectNoPageErrors,
  expectTaskInTable,
  expectTaskNotInTable,
  fillTaskForm,
  navigateToNewTask,
  navigateToTaskList,
  openTaskFromList,
  saveTask,
  searchForTask,
  uniqueTitle,
  waitForReactApp,
} from "../../utils/reactTestUtils";

/** Provides Playwright helper logic for the React notification Snackbar/Alert. */
async function expectNotification(page: Page, textFragment: string, timeout = 15_000) {
  await expect(page.getByRole("alert")).toContainText(textFragment, { timeout });
}

/**
 * Full Task CRUD lifecycle exercised through the React + TypeScript UI.
 *
 * Prerequisites:
 *   1. PLAYWRIGHT_REACT_URL or TASKFLOW_REACT_BASE_URL points at the hosted React app
 *   2. Gateway/API running with the normal TaskFlow Aspire stack
 *   3. `npx playwright install chromium`
 *
 * Run:  npm run test:react
 */

test.describe("TaskFlow React - Task CRUD lifecycle", () => {
  test.describe.configure({ mode: "serial" });

  let taskTitle: string;
  let updatedTitle: string;
  let checklistTitle: string;
  let commentBody: string;
  let pageErrors: Error[];

  test.beforeAll(() => {
    taskTitle = uniqueTitle("E2E-React-Create");
    updatedTitle = uniqueTitle("E2E-React-Updated");
    checklistTitle = uniqueTitle("E2E-React-Checklist");
    commentBody = uniqueTitle("E2E-React-Comment");
  });

  test.beforeEach(async ({ page }) => {
    pageErrors = [];
    page.on("pageerror", (error) => pageErrors.push(error));
    page.setDefaultTimeout(30_000);
  });

  test.afterEach(async () => {
    await expectNoPageErrors(pageErrors);
  });

  /** Verifies the Playwright scenario for 1. create a new task. */
  test("1. create a new task", async ({ page }) => {
    await waitForReactApp(page);
    await navigateToNewTask(page);

    await fillTaskForm(page, {
      description: "Automated Playwright E2E test task (React)",
      title: taskTitle,
    });

    await saveTask(page);
    await expect(page.getByRole("heading", { name: /edit task/i })).toBeVisible({ timeout: 15_000 });
    await expect(page.locator('input[placeholder="Add item"]:visible')).toBeVisible({ timeout: 15_000 });

    await addChecklistItem(page, checklistTitle);
    await addComment(page, commentBody);

    await navigateToTaskList(page);
    await searchForTask(page, taskTitle);
    await expectTaskInTable(page, taskTitle, 20_000);
  });

  /** Verifies the Playwright scenario for 2. read the created task and child details. */
  test("2. read the created task and child details", async ({ page }) => {
    await navigateToTaskList(page);
    await searchForTask(page, taskTitle);
    await expectTaskInTable(page, taskTitle);

    const row = page.locator("tbody tr", { hasText: taskTitle }).first();
    await expect(row).toContainText("Open");
    await expect(row).toContainText("Medium");

    await openTaskFromList(page, taskTitle);
    await expect(page.getByText(checklistTitle)).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText(commentBody)).toBeVisible({ timeout: 10_000 });
  });

  /** Verifies the Playwright scenario for 3. update the task title and priority. */
  test("3. update the task title and priority", async ({ page }) => {
    await navigateToTaskList(page);
    await searchForTask(page, taskTitle);
    await openTaskFromList(page, taskTitle);

    await fillTaskForm(page, {
      priority: "High",
      title: updatedTitle,
    });
    await saveTask(page);
    await expect(page.getByText("Task saved.")).toBeVisible({ timeout: 15_000 });

    await navigateToTaskList(page);
    await searchForTask(page, updatedTitle);
    await expectTaskInTable(page, updatedTitle);
    await expectTaskNotInTable(page, taskTitle);
    await expect(page.locator("tbody tr", { hasText: updatedTitle }).first()).toContainText("High");
  });

  /** Verifies the Playwright scenario for 4. delete the task. */
  test("4. delete the task", async ({ page }) => {
    await navigateToTaskList(page);
    await searchForTask(page, updatedTitle);
    await deleteTaskFromList(page, updatedTitle);
    await expect(page.getByText("Task deleted.")).toBeVisible({ timeout: 15_000 });
    await expectTaskNotInTable(page, updatedTitle);
  });
});

test.describe("TaskFlow React - two-tab optimistic concurrency (412)", () => {
  /** Editing the same task from two tabs surfaces a 412 on the second save instead of an overwrite. */
  test("editing the same task from two tabs surfaces a 412 on the second save", async ({ page, context }) => {
    const title = uniqueTitle("E2E-React-Conflict");

    // Tab A creates the task and stays on its edit page (holds the just-loaded Version/ETag).
    await waitForReactApp(page);
    await navigateToNewTask(page);
    await fillTaskForm(page, { title });
    await saveTask(page);
    await expect(page.getByRole("heading", { name: /edit task/i })).toBeVisible({ timeout: 15_000 });
    const taskUrl = page.url();

    // Tab B loads the same task independently, capturing the same If-Match currency as tab A.
    const pageB = await context.newPage();
    await pageB.goto(taskUrl, { waitUntil: "domcontentloaded" });
    await expect(pageB.getByRole("heading", { name: /edit task/i })).toBeVisible({ timeout: 15_000 });

    // Tab A saves first: succeeds and bumps the server-side Version past what tab B is holding.
    await fillTaskForm(page, { priority: "High" });
    await saveTask(page);
    await expect(page.getByRole("combobox", { name: "Priority" })).toContainText("High");

    // Tab B saves against its now-stale Version: the API returns 412, and the UI reports the
    // conflict and reloads instead of silently overwriting tab A's change.
    await fillTaskForm(pageB, { priority: "Low" });
    await saveTask(pageB);
    await expectNotification(pageB, "Task changed elsewhere, reloading.");

    await pageB.close();

    // Cleanup.
    await navigateToTaskList(page);
    await searchForTask(page, title);
    await deleteTaskFromList(page, title);
    await expectTaskNotInTable(page, title);
  });
});
