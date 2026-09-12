import { expect, type Page } from "@playwright/test";

function requireBaseUrl() {
  if (!process.env.PLAYWRIGHT_REACT_URL && !process.env.TASKFLOW_REACT_BASE_URL) {
    throw new Error("PLAYWRIGHT_REACT_URL or TASKFLOW_REACT_BASE_URL is required for direct React Playwright runs. Use the C# Aspire wrapper or set one of those variables.");
  }
}

/** Provides Playwright helper logic for wait for react app. */
export async function waitForReactApp(page: Page) {
  requireBaseUrl();
  await page.goto("/tasks", { waitUntil: "domcontentloaded" });
  await expect(page.getByRole("heading", { exact: true, name: "Tasks" })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole("navigation")).toContainText("TaskFlow");
}

/** Provides Playwright helper logic for navigate to task list. */
export async function navigateToTaskList(page: Page) {
  requireBaseUrl();
  await page.goto("/tasks", { waitUntil: "domcontentloaded" });
  await expect(page.getByRole("heading", { exact: true, name: "Tasks" })).toBeVisible({ timeout: 10_000 });
}

/** Provides Playwright helper logic for navigate to new task. */
export async function navigateToNewTask(page: Page) {
  await page.getByRole("link", { name: /new task/i }).or(page.getByRole("button", { name: /new task/i })).first().click();
  await expect(page.getByRole("heading", { name: /new task/i })).toBeVisible({ timeout: 10_000 });
}

/** Provides Playwright helper logic for search for task. */
export async function searchForTask(page: Page, term: string) {
  await page.getByLabel("Search").fill(term);
  await page.getByRole("button", { name: /^search$/i }).click();
}

/** Provides Playwright helper logic for expect task in table. */
export async function expectTaskInTable(page: Page, title: string, timeout = 15_000) {
  await expect(page.locator("table")).toContainText(title, { timeout });
}

/** Provides Playwright helper logic for expect task not in table. */
export async function expectTaskNotInTable(page: Page, title: string, timeout = 15_000) {
  await expect(page.locator("body")).not.toContainText(title, { timeout });
}

/** Provides Playwright helper logic for open task from list. */
export async function openTaskFromList(page: Page, title: string) {
  const row = page.locator("tbody tr", { hasText: title }).first();
  await expect(row).toBeVisible({ timeout: 10_000 });
  await row.getByRole("link", { name: /edit task/i }).click();
  await expect(page.getByRole("heading", { name: /edit task/i })).toBeVisible({ timeout: 10_000 });
  for (const section of [/^Checklist \(\d+\)$/, /^Comments \(\d+\)$/]) {
    const accordion = page.getByRole("button", { name: section });
    if ((await accordion.getAttribute("aria-expanded")) !== "true") {
      await accordion.click();
    }
  }
}

/** Provides Playwright helper logic for delete task from list. */
export async function deleteTaskFromList(page: Page, title: string) {
  const row = page.locator("tbody tr", { hasText: title }).first();
  await expect(row).toBeVisible({ timeout: 10_000 });
  await row.getByRole("button", { name: /delete task/i }).click();
  await confirmDialog(page, "Delete");
}

/** Provides Playwright helper logic for fill task form. */
export async function fillTaskForm(page: Page, values: {
  title?: string;
  description?: string;
  priority?: string;
  status?: string;
}) {
  if (values.title !== undefined) await page.getByLabel("Title").fill(values.title);
  if (values.description !== undefined) await page.getByLabel("Description").fill(values.description);
  if (values.priority !== undefined) await selectMuiOption(page, "Priority", values.priority);
  if (values.status !== undefined) await selectMuiOption(page, "Status", values.status);
}

/** Provides Playwright helper logic for add checklist item. */
export async function addChecklistItem(page: Page, title: string) {
  const checklist = page.getByRole("button", { name: /^Checklist \(\d+\)$/ });
  if ((await checklist.getAttribute("aria-expanded")) !== "true") {
    await checklist.click();
  }
  const input = page.locator('input[placeholder="Add item"]:visible');
  await input.fill(title);
  const responsePromise = page.waitForResponse(
    (response) => response.url().includes("/checklist-items") && response.request().method() === "POST" && response.status() === 201,
  );
  await input.press("Enter");
  await responsePromise;
  await expect(page.getByText(title)).toBeVisible({ timeout: 5_000 });
}

/** Provides Playwright helper logic for add comment. */
export async function addComment(page: Page, body: string) {
  const comments = page.getByRole("button", { name: /^Comments \(\d+\)$/ });
  if ((await comments.getAttribute("aria-expanded")) !== "true") {
    await comments.click();
  }
  const input = page.locator('textarea[placeholder="Add a comment"]:visible');
  await input.fill(body);
  const responsePromise = page.waitForResponse(
    (response) => response.url().includes("/comments") && response.request().method() === "POST" && response.status() === 201,
  );
  await input.locator("xpath=ancestor::*[@role='region'][1]").getByRole("button", { name: /^add$/i }).click();
  await responsePromise;
  await expect(page.getByText(body)).toBeVisible({ timeout: 5_000 });
}

/** Provides Playwright helper logic for save task. */
export async function saveTask(page: Page) {
  const responsePromise = page.waitForResponse(
    (response) => response.url().includes("/task-items") && ["POST", "PUT"].includes(response.request().method()),
  );
  await page.getByRole("button", { name: /^save$/i }).click();
  await responsePromise;
}

/** Provides Playwright helper logic for confirm dialog. */
export async function confirmDialog(page: Page, confirmLabel: string) {
  const dialog = page.getByRole("dialog");
  await expect(dialog).toBeVisible({ timeout: 10_000 });
  await dialog.getByRole("button", { name: confirmLabel }).click();
}

/** Provides Playwright helper logic for select mui option. */
export async function selectMuiOption(page: Page, label: string, option: string) {
  await page.getByLabel(label).click();
  await page.getByRole("option", { name: option }).click();
}

/** Provides Playwright helper logic for expect no page errors. */
export async function expectNoPageErrors(errors: Error[] = []) {
  expect(errors.map((error) => error.message)).toEqual([]);
}

/** Provides Playwright helper logic for unique title. */
export function uniqueTitle(prefix: string) {
  return `${prefix}-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
}
