import { test, expect } from "@playwright/test";

const catalog = {
  revision: 7,
  categories: [
    {
      id: "delivery",
      title: "Delivery",
      path: "Services › Delivery",
      description: "About receiving services",
      questions: [
        {
          id: "satisfaction",
          label: "How satisfied are you with the delivery experience?",
          type: "rating",
          required: true,
        },
        { id: "effort", label: "How easy was it to receive the service?", type: "effort", required: true },
        {
          id: "experience",
          label: "How was the delivery experience?",
          type: "text",
          required: true,
        },
        {
          id: "improvement",
          label: "What change would improve your experience?",
          type: "text",
          required: true,
        },
      ],
    },
  ],
};
test.beforeEach(async ({ page }) => {
  await page.route("**/v1/**", (route) =>
    route.fulfill({ status: 404, json: { error: "unmocked" } }),
  );
  await page.route("**/v1/portal/auth/me", (route) =>
    route.fulfill({
      json: {
        customer_id: "C-maryam",
        username: "maryam",
        display_name: "Maryam Ahmadi",
      },
    }),
  );
  await page.route("**/v1/portal/categories", (route) =>
    route.fulfill({ json: catalog }),
  );
});

test("complaint submission preserves exactly the same payload after a failure", async ({
  page,
}) => {
  const bodies: Record<string, unknown>[] = [];
  await page.route("**/v1/portal/submissions", (route) => {
    bodies.push(route.request().postDataJSON());
    return route.fulfill(
      bodies.length === 1
        ? { status: 503, json: { error: "Temporary sending error" } }
        : { status: 202, json: { receipt: bodies[0].submission_id } },
    );
  });
  await page.goto("/complaints");
  await page.getByLabel("What is your experience related to?").selectOption("delivery");
  await page.getByLabel("Title of complaint").fill("Delay in receiving services");
  await page
    .getByLabel("What happened and what do you expect?")
    .fill("The service was received late; Please follow up.");
  await page.getByRole("button", { name: "Send a complaint" }).click();
  await expect(page.getByRole("alert")).toContainText("Temporary sending error");
  await expect(page.getByLabel("Title of complaint")).toBeDisabled();
  await page.getByRole("button", { name: "Retrying to send" }).click();
  await expect(
    page.getByRole("heading", { name: "Your complaint has been received." }),
  ).toBeVisible();
  expect(bodies).toHaveLength(2);
  expect(bodies[1]).toEqual(bodies[0]);
  expect(bodies[0]).toMatchObject({
    kind: "complaint",
    category: "delivery",
    revision: 7,
  });
});

test("VOC uses database category questions and permits NPS zero", async ({
  page,
}) => {
  let body: Record<string, unknown> | undefined;
  await page.route("**/v1/portal/submissions", (route) => {
    body = route.request().postDataJSON();
    return route.fulfill({
      status: 202,
      json: { receipt: body!.submission_id },
    });
  });
  await page.goto("/voice-of-customer");
  await expect(page.getByText("Your questions will be displayed after selecting the topic.")).toBeVisible();
  await page.getByLabel("What is your experience related to?").selectOption("delivery");
  await page
    .getByRole("group", { name: "How satisfied are you with the delivery experience?" })
    .getByRole("radio", { name: "Dissatisfied", exact: true })
    .check();
  await page.getByLabel("How was the delivery experience?").fill("Delivery was late");
  await page.getByRole("group", { name: "How easy was it to receive the service?" }).getByRole("radio", { name: "7", exact: true }).check();
  await page
    .getByLabel("What change would improve your experience?")
    .fill("More detailed information");
  await page
    .getByRole("group", { name: "How likely" })
    .getByRole("radio", { name: "0", exact: true })
    .check();
  await page.getByRole("button", { name: "Sending the questionnaire for analysis" }).click();
  await expect(
    page.getByRole("heading", { name: "Your responses have been received." }),
  ).toBeVisible();
  expect(body).toMatchObject({
    kind: "voc",
    nps_score: 0,
    answers: {
      satisfaction: "2",
      effort: "7",
      experience: "Delivery was late",
      improvement: "More detailed information",
    },
  });
  await page.getByRole("button", { name: "Record another experience" }).click();
  await expect(page.getByLabel("What is your experience related to?")).toHaveValue("");
});

test("feedback uses the logged-in identity without contact or consent controls", async ({
  page,
}) => {
  const bodies: Record<string, unknown>[] = [];
  await page.route("**/v1/portal/submissions", (route) => {
    bodies.push(route.request().postDataJSON());
    return route.fulfill({
      status: 202,
      json: { receipt: bodies.at(-1)!.submission_id },
    });
  });
  await page.goto("/feedbacks");
  await page.getByLabel("What is your experience related to?").selectOption("delivery");
  await page.getByLabel("What do you want to share with us?").fill("I had a good experience");
  await page.getByRole("radio", { name: "Very satisfied", exact: true }).check();
  await page.getByRole("group", { name: "How easy was it to do the job or get the service?" }).getByRole("radio", { name: "1", exact: true }).check();
  await page.getByRole("button", { name: "Send feedback" }).click();
  await expect(
    page.getByRole("heading", { name: "Your feedback has been received." }),
  ).toBeVisible();
  expect(bodies[0]).toMatchObject({ kind: "feedback", rating: 5, ces_score: 1 });
  expect(bodies[0]).not.toHaveProperty("name");
  expect(bodies[0]).not.toHaveProperty("contact");
  expect(bodies[0]).not.toHaveProperty("consent");
  await expect(page.getByRole("checkbox")).toHaveCount(0);
  await expect(page.locator(".header, .intro, .sidebar")).toHaveCount(0);
  await page.getByRole("button", { name: "Record another experience" }).click();
  await expect(page.getByLabel("What do you want to share with us?")).toHaveValue("");
});

test("login rejects wrong credentials, persists across reload and supports logout", async ({
  page,
}) => {
  let signedIn = false;
  const user = {
    customer_id: "C-maryam",
    username: "maryam",
    display_name: "Maryam Ahmadi",
  };
  await page.route("**/v1/portal/auth/me", (route) =>
    route.fulfill(signedIn ? { json: user } : { status: 401, json: {} }),
  );
  await page.route("**/v1/portal/auth/login", (route) => {
    const body = route.request().postDataJSON();
    signedIn =
      body.username === "maryam" && body.password === "correct-password";
    return route.fulfill(
      signedIn
        ? { json: user }
        : { status: 401, json: { error: "The password is incorrect" } },
    );
  });
  await page.route("**/v1/portal/auth/logout", (route) => {
    signedIn = false;
    return route.fulfill({ status: 204 });
  });
  await page.goto("/complaints");
  await expect(page.getByRole("combobox")).toHaveCount(0);
  await page.getByLabel("Username", { exact: true }).fill("maryam");
  await page.getByLabel("Password", { exact: true }).fill("wrong");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("The password is incorrect");
  await page.getByLabel("Password", { exact: true }).fill("correct-password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(page.getByLabel("Title of complaint")).toBeVisible();
  await expect(page.locator(".account-bar")).toContainText(user.display_name);
  await page.reload();
  await expect(page.getByLabel("Title of complaint")).toBeVisible();
  await page.getByRole("button", { name: "Sign out" }).click();
  await expect(
    page.getByRole("heading", { name: "Entering the customer portal" }),
  ).toBeVisible();
});

test("expired sessions return to login", async ({ page }) => {
  await page.route("**/v1/portal/categories", (route) =>
    route.fulfill({ status: 401, json: {} }),
  );
  await page.goto("/feedbacks");
  await expect(
    page.getByRole("heading", { name: "Entering the customer portal" }),
  ).toBeVisible();
  await expect(page.getByRole("combobox")).toHaveCount(0);
});

test("database failure can be retried and an empty taxonomy never offers invented categories", async ({
  page,
}) => {
  await page.route("**/v1/portal/categories", (route) =>
    route.fulfill({ status: 503, json: { error: "It is not possible to receive topics" } }),
  );
  await page.goto("/feedbacks");
  await expect(page.getByRole("alert")).toContainText("receive topics");
  await page.route("**/v1/portal/categories", (route) =>
    route.fulfill({ json: { revision: 8, categories: [] } }),
  );
  await page.getByRole("button", { name: "Retry" }).click();
  await expect(
    page.getByRole("heading", { name: "No subject has yet been defined to record the experience." }),
  ).toBeVisible();
  await expect(page.getByRole("combobox")).toHaveCount(0);
});

test("all three sections work on a phone without horizontal overflow", async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/feedbacks");
  for (const label of ["Complaints", "Feedback", "Voice of customer"]) {
    await page
      .getByRole("navigation")
      .getByRole("link", { name: new RegExp(label) })
      .click();
    await page.getByLabel("What is your experience related to?").selectOption("delivery");
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
  }
  await page.screenshot({
    path: "test-results/portal-mobile.png",
    fullPage: true,
  });
});

test("desktop portal preview", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1100 });
  await page.goto("/feedbacks");
  await expect(
    page.getByRole("heading", { name: "Submit feedback", exact: true }),
  ).toBeVisible();
  await page.screenshot({
    path: "test-results/portal-desktop.png",
    fullPage: true,
  });
});

test("a complaint can be recorded by voice and is sent with its answers as one multipart request", async ({
  page,
}) => {
  const requests: { contentType: string; body: string }[] = [];
  await page.route("**/v1/portal/voice", (route) => {
    const request = route.request();
    requests.push({
      contentType: request.headers()["content-type"] ?? "",
      body: request.postDataBuffer()?.toString("latin1") ?? "",
    });
    return route.fulfill({ status: 202, json: { receipt: "receipt-1" } });
  });
  await page.goto("/complaints");
  await page.getByLabel("What is your experience related to?").selectOption("delivery");
  await page.getByLabel("Title of complaint").fill("Delay in receiving services");
  await page.getByRole("radio", { name: "Audio recording" }).click();
  await expect(page.getByLabel("What happened and what do you expect?")).toHaveCount(0);

  await page.getByRole("button", { name: "Sending a voice message" }).click();
  await expect(page.getByRole("alert")).toContainText("First, record your voice message");
  expect(requests).toHaveLength(0);

  await page.getByRole("button", { name: /Start recording/ }).click();
  await expect(page.locator(".recorder-live")).toBeVisible();
  await page.waitForTimeout(1500);
  await page.getByRole("button", { name: /End of recording/ }).click();
  await expect(page.locator(".recorder-preview audio")).toBeVisible();
  await expect(page.getByText("First, record your voice message")).toHaveCount(0);
  await page.locator(".form-card").screenshot({
    path: "test-results/portal-voice.png",
  });

  await page.getByRole("button", { name: "Sending a voice message" }).click();
  await expect(
    page.getByRole("heading", { name: "Your complaint has been received." }),
  ).toBeVisible();
  expect(requests).toHaveLength(1);
  expect(requests[0].contentType).toContain("multipart/form-data");
  expect(requests[0].body).toContain('name="submission"');
  expect(requests[0].body).toContain('"kind":"complaint"');
  expect(requests[0].body).toContain('"comment":""');
  expect(requests[0].body).toContain('name="duration_ms"');
  expect(requests[0].body).toMatch(
    /name="audio"[^]*Content-Type: audio\/wav[^]*RIFF/,
  );
});
