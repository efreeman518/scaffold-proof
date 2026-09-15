import { expect, Page } from "@playwright/test";

/**
 * MudBlazor renders standard HTML. Key selectors:
 *   - MudTable rows: .mud-table-body tr
 *   - MudButton:     button.mud-button-root
 *   - MudTextField:  .mud-input-control input
 *   - MudSelect:     .mud-select (click to open popover, then .mud-popover .mud-list-item)
 *   - Dialog:        .mud-dialog
 */

// ---------------------------------------------------------------------------
// Navigation
// ---------------------------------------------------------------------------

function requireBaseUrl() {
  if (!process.env.PLAYWRIGHT_BLAZOR_URL) {
    throw new Error("PLAYWRIGHT_BLAZOR_URL is required for direct Blazor Playwright runs. Use the C# Aspire wrapper or set PLAYWRIGHT_BLAZOR_URL.");
  }
}

export async function waitForApp(page: Page) {
  requireBaseUrl();
  await page.goto("/tasks", { waitUntil: "domcontentloaded" });
  await expect(page.getByRole("heading", { name: "Tasks" })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole("button", { name: /new task/i })).toBeVisible({ timeout: 15_000 });
}

/** Provides Playwright helper logic for navigate to new task. */
export async function navigateToNewTask(page: Page) {
  await expect(async () => {
    await page.getByRole("button", { name: /new task/i }).click();
    await expect(page).toHaveURL(/\/tasks\/new$/i, { timeout: 1_000 });
  }).toPass({ timeout: 15_000 });
  await expect(page.getByRole("heading", { name: /new task/i })).toBeVisible({ timeout: 15_000 });
  await expect(getLabeledField(page, "Title")).toBeVisible({ timeout: 15_000 });
}

/** Provides Playwright helper logic for navigate to task list. */
export async function navigateToTaskList(page: Page) {
  requireBaseUrl();
  await page.goto("/tasks", { waitUntil: "domcontentloaded" });
  await expect(page.locator(".mud-table")).toBeVisible({ timeout: 10_000 });
}

/** Provides Playwright helper logic for search for task. */
export async function searchForTask(page: Page, term: string) {
  const searchInput = page.getByPlaceholder(/title or description/i);
  await searchInput.click();
  await searchInput.fill(term);
  await page.getByRole("button", { name: /^search$/i }).click();
}

// ---------------------------------------------------------------------------
// Form helpers
// ---------------------------------------------------------------------------

export async function fillTextField(page: Page, label: string, value: string) {
  const field = getLabeledField(page, label);
  await expect(field).toBeVisible({ timeout: 30_000 });
  await field.click();
  await field.fill(value);
}

/** Provides Playwright helper logic for select option. */
export async function selectOption(page: Page, label: string, option: string) {
  // MudSelect renders a hidden <input> inside .mud-input-control.
  // Click the visible .mud-input wrapper to open the dropdown.
  const wrapper = page.locator(`.mud-input-control:has(label:has-text("${label}"))`);
  await wrapper.locator(".mud-input").first().click();
  // Wait for popover and click the option
  const popover = page.locator(".mud-popover-open");
  await popover.locator(`.mud-list-item:has-text("${option}")`).click();
}

/** Provides Playwright helper logic for click save. */
export async function clickSave(page: Page) {
  await page.getByRole("button", { name: /save/i }).click();
}

/** Provides Playwright helper logic for saving a new task. */
export async function clickSaveNewTask(page: Page) {
  await page.getByRole("button", { name: /save/i }).click();
  await expect(page).toHaveURL(/\/tasks\/[0-9a-f-]{36}$/i, { timeout: 30_000 });
  await expect(page.getByRole("heading", { name: /edit task/i })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText(/ID: [0-9a-f-]{36}/i)).toBeVisible({ timeout: 15_000 });
}

// ---------------------------------------------------------------------------
// Table helpers
// ---------------------------------------------------------------------------

export async function getTableRowByText(page: Page, text: string) {
  return page.locator(`.mud-table-body tr:has-text("${text}")`);
}

/** Provides Playwright helper logic for expect task in table. */
export async function expectTaskInTable(page: Page, title: string, timeout = 10_000) {
  await expect(page.locator(`.mud-table-body`)).toContainText(title, { timeout });
}

/** Provides Playwright helper logic for expect task not in table. */
export async function expectTaskNotInTable(page: Page, title: string, timeout = 10_000) {
  await expect(page.locator(`.mud-table-body`)).not.toContainText(title, { timeout });
}

/** Provides Playwright helper logic for click edit on row. */
export async function clickEditOnRow(page: Page, title: string) {
  const row = page.locator(`.mud-table-body tr:has-text("${title}")`);
  // Actions cell has 3 icon buttons: checkmark (0), edit (1), delete (2)
  await row.locator("td").last().locator("button").nth(1).click();
  await expect(page.getByRole("heading", { name: /edit task/i })).toBeVisible({ timeout: 10_000 });
}

/** Provides Playwright helper logic for click delete on row. */
export async function clickDeleteOnRow(page: Page, title: string) {
  const row = page.locator(`.mud-table-body tr:has-text("${title}")`);
  // Actions cell has 3 icon buttons: checkmark (0), edit (1), delete (2)
  await row.locator("td").last().locator("button").nth(2).click();
}

// ---------------------------------------------------------------------------
// Dialog helpers
// ---------------------------------------------------------------------------

export async function confirmDeleteDialog(page: Page) {
  const dialog = page.locator(".mud-overlay-dialog, .mud-dialog-container").first();
  await expect(dialog).toBeVisible({ timeout: 15_000 });
  await dialog.getByRole("button", { name: /delete/i }).click();
}

// ---------------------------------------------------------------------------
// Snackbar / feedback
// ---------------------------------------------------------------------------

export async function expectSnackbar(page: Page, textFragment: string, timeout = 5_000) {
  await expect(page.getByRole("alert").filter({ hasText: textFragment }).last()).toBeVisible({ timeout });
}

// ---------------------------------------------------------------------------
// Unique test data
// ---------------------------------------------------------------------------

export function uniqueTitle(prefix: string) {
  return `${prefix}-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
}

function getLabeledField(page: Page, label: string) {
  return page.getByLabel(new RegExp(`^${escapeRegExp(label)}\\*?$`, "i")).first();
}

function escapeRegExp(value: string) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
