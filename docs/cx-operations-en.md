# Customer Experience Operations

Immigration `007_cx_operations.sql` Adds files, teams, calendar deadlines, notifications, shared issues, automated rules, survey invitations, and travel stages to the database.. The host applies migration when it starts; service `query` The processor executes the operation every 30 seconds. Lock PostgreSQL And unique keys prevent simultaneous execution and repeated notification.

## Setting up team work

1. System manager in «Case settings» Create the account of experts and supervisors and make them team members. The person in charge of the case must be active and a member of the selected team. Deleting the last active administrator or deleting a member with an open file is not allowed.

2. Set initial response and problem resolution deadlines for four severities. Default: Saturday to Wednesday, 9 a.m. to 5 p.m. Tehran time; answer/Critical solution 30/240, more than 60/480, average 240/1440 and low 480/2400 working minutes. The holidays are selected by the solar calendar and in the database by date ISO are stored. Each calendar file keeps its creation time; Editing a policy does not change the calendar of previous cases.

3. A customer complaint is automatically converted into a file. Start time is the time to receive feedback. Feedback does not require review and a new file; Previous files linked to the feedback are still visible. Each feedback is attached to only one file and retries do not create another file.

4. Refer the case to the team and expert. The answer and the public file can be seen on the portal; Internal file description, note and internal file are not displayed to the client. The outcome of the proceedings is mandatory when resolving or closing. The customer can confirm the solution or reopen the case by mentioning the reason. Simultaneous change of two users is controlled by file version checking.

## Accessions

| role | Operation access |
|---|---|
| `admin` | All operations, team management and staff accounts |
| `cx_lead` And `support_supervisor` | All organization files, setting deadlines and rules, campaign, travel and common problems |
| `cx_analyst` | His case, his team and the queue without a manager; Answers, notes, attachments and proceedings; Reports and priority queue |
| `data_ai_operator` | Data Access and Artificial Intelligence; He does not become a member of the proceedings and is responsible for the case |
| `customer` | Only requests, messages, public files and survey invitations from the same customer |

Accesses are controlled on the server side and all reads and writes are limited to the organization. Deactivating the account or changing the role will invalidate the previous session.

## Deadline and referral

Working hours and holidays are calculated in both deadlines. «Awaiting customer reply» It only stops the solution deadline; The client message or file continues it. In the last 20% of the deadline, the experts and managers will be notified. Exceeding the deadline will generate a separate notification and refer the team member’s case to the active supervisor of the same team.. Reopening creates a new deadline for resolution; First response and first resolution criteria are maintained for reporting.

## Common rules and problems

The rule can examine the topic and subtopics, the analyzed severity, the channel and the number of repetitions of the complaint in 30 days and create a file, refer or create a notification.. Rules only review complaints received after they are activated; History of successful execution or error can be seen on the same page. Human modification of the file takes precedence over initial automatic referral.

For a common problem, record the owner, issue, root cause, and corrective action and attach related files.. When announcing the solution, the cause and action are required. The comparison before and after the resolution of the complaint feedback is made with the related topic or the feedback connected to the files and compares two equal intervals up to 14 full days; This number alone does not prove the causal effect of the action.

## Survey and travel

Campaigns CSAT From 1 to 5, CES From 1 to 7 and NPS They receive from zero to 10. The invitation can be manual, after case resolution or after certain event. Re-invitation interval, reminder and expiration are adjustable. For each customer, in addition to the interval of the same campaign, the limit of one invitation per day is observed. Rejected or expired invitations cannot be answered; Resubmitting the same answer does not create another record.

Survey response with feedback, event and outbox It is stored in a transaction and enters the existing path of analysis and reports. Notifications and invitations are now provided in the panel and portal; Sending email or SMS is not connected in this version.

The journey consists of 2 to 12 regular stages and the names of the events of each stage. The funnel only counts actual customer events in order of journey stage and deadline. The customer is still within the deadline with «Waiting» And the customer passed the deadline with «Falling» is displayed. The satisfaction of each stage is calculated only from non-complaint feedbacks in the same interval.

## Reading reports

«Service outcomes» The files received in the period with the response and solution time in terms of working minutes, compliance with the deadline, the number of reopening and CSAT The poll displays after solving. The unanswered or unsolved case is not included in the percentage denominator and the unregistered score is not considered zero.. The grouping is based on the team and the current topic of the case.

«Follow-up customers» Points up to 100 only from last 30 day complaints (Each complaint 8, ceiling 32), open complaint files (Each file 7, ceiling 28) And backlogged complaint files (Each file 20, ceiling 40) It makes. Reasons and cases are displayed next to the score. This score is the priority of handling and not the predicted probability of falling.

## Development validation

Unit tests in `Cx.UnitTests`, test transactions and operation calculations in `Cx.IntegrationTests/OperationsTests.cs` and test the interfaces in the file `operations.spec.ts` Both are web projects. To use PostgreSQL available, `CX_TEST_POSTGRES` to a separate base on the same service; In this case, the integration test does not create a new container. Building and restarting only two application services is required and infrastructure data is preserved.

## Showing files and grades

Complaints are registered from the complaints fund. file page description and attachments, the place to register the answer,
and fevers «All activities», «Answers and notes» And «History of changes» has.
The team, responsible, status, intensity, steps and deadlines are in the side column; Specification changes are recorded with the previous and new value.
The response to the client is public and the internal note is for the team only; The name of the author, the time and the processing stage can be seen in the history.
«Registration of problem solving» Confirms the case as resolved and pending; In file editing, final closing and reopening with a reason is possible.
All panel tables from `GridPanel`, `GridFilters` And `DataTable` they use; Lists cursor Previous control/Dimension and the size of the page are common.
The topic tree, with shared paging control, pages the main topics and keeps each branch with its subtopics..
