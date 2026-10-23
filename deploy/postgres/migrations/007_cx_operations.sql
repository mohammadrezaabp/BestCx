-- Operational CX: tenant-scoped ownership, closed-loop cases and measurable outcomes.
ALTER TABLE app_user DROP CONSTRAINT app_user_role_check;
ALTER TABLE app_user DROP CONSTRAINT app_user_check;
ALTER TABLE app_user ALTER COLUMN role TYPE varchar(32);
ALTER TABLE app_user ADD CONSTRAINT app_user_role_check CHECK (role IN ('customer','admin','cx_analyst','cx_lead','support_supervisor','data_ai_operator'));
ALTER TABLE app_user ADD CONSTRAINT app_user_identity_check CHECK ((role = 'customer' AND customer_id IS NOT NULL) OR (role <> 'customer' AND customer_id IS NULL));

CREATE TABLE cx_team (
 id uuid PRIMARY KEY, tenant_id varchar(64) NOT NULL, name varchar(128) NOT NULL,
 lead_id uuid REFERENCES app_user(id), enabled boolean NOT NULL DEFAULT true,
 UNIQUE(tenant_id,id), UNIQUE(tenant_id,name)
);
CREATE TABLE cx_team_member (
 tenant_id varchar(64) NOT NULL, team_id uuid NOT NULL, user_id uuid NOT NULL REFERENCES app_user(id),
 PRIMARY KEY(team_id,user_id), FOREIGN KEY(tenant_id,team_id) REFERENCES cx_team(tenant_id,id) ON DELETE CASCADE
);
CREATE TABLE cx_sla_policy (
 id uuid PRIMARY KEY, tenant_id varchar(64) NOT NULL, name varchar(128) NOT NULL,
 priority varchar(16) NOT NULL CHECK(priority IN ('critical','high','medium','low')),
 response_minutes integer NOT NULL CHECK(response_minutes BETWEEN 1 AND 525600),
 resolution_minutes integer NOT NULL CHECK(resolution_minutes BETWEEN 1 AND 525600),
 calendar jsonb NOT NULL, enabled boolean NOT NULL DEFAULT true,
 UNIQUE(tenant_id,id), UNIQUE(tenant_id,priority)
);
CREATE TABLE cx_problem (
 id uuid PRIMARY KEY, tenant_id varchar(64) NOT NULL, title varchar(256) NOT NULL,
 topic varchar(128), root_cause text NOT NULL DEFAULT '', improvement text NOT NULL DEFAULT '',
 status varchar(16) NOT NULL DEFAULT 'open' CHECK(status IN ('open','investigating','fixed','closed')),
 owner_id uuid REFERENCES app_user(id), created_at timestamptz NOT NULL DEFAULT now(), fixed_at timestamptz,
 version integer NOT NULL DEFAULT 1, UNIQUE(tenant_id,id)
);
CREATE TABLE cx_case (
 id uuid PRIMARY KEY, tenant_id varchar(64) NOT NULL, reference varchar(32) NOT NULL,
 customer_id varchar(128) NOT NULL, title varchar(256) NOT NULL, description text NOT NULL,
 topic varchar(128), priority varchar(16) NOT NULL CHECK(priority IN ('critical','high','medium','low')),
 status varchar(24) NOT NULL DEFAULT 'new' CHECK(status IN ('new','in_progress','waiting_customer','resolved','closed')),
 team_id uuid, assignee_id uuid REFERENCES app_user(id), problem_id uuid,
 sla_policy_id uuid, sla_calendar jsonb NOT NULL, response_minutes integer NOT NULL, resolution_minutes integer NOT NULL,
 response_due_at timestamptz NOT NULL, resolution_due_at timestamptz NOT NULL,
 resolution_started_at timestamptz NOT NULL DEFAULT now(),
 first_response_at timestamptz, first_resolved_at timestamptz, resolved_at timestamptz, closed_at timestamptz,
 response_business_minutes double precision, resolution_business_minutes double precision,
 response_within_sla boolean, resolution_within_sla boolean,
 pause_started_at timestamptz, paused_minutes double precision NOT NULL DEFAULT 0,
 resolution_text text NOT NULL DEFAULT '', reopen_count integer NOT NULL DEFAULT 0,
 created_by varchar(128) NOT NULL, created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
 version integer NOT NULL DEFAULT 1, UNIQUE(tenant_id,id), UNIQUE(tenant_id,reference),
 FOREIGN KEY(tenant_id,customer_id) REFERENCES customer_identity(tenant_id,customer_id),
 FOREIGN KEY(tenant_id,team_id) REFERENCES cx_team(tenant_id,id),
 FOREIGN KEY(tenant_id,problem_id) REFERENCES cx_problem(tenant_id,id),
 FOREIGN KEY(tenant_id,sla_policy_id) REFERENCES cx_sla_policy(tenant_id,id)
);
CREATE INDEX cx_case_queue ON cx_case(tenant_id,status,updated_at DESC);
CREATE INDEX cx_case_customer ON cx_case(tenant_id,customer_id,created_at DESC);
CREATE INDEX cx_case_owner ON cx_case(tenant_id,assignee_id,status);
CREATE TABLE cx_case_feedback (
 tenant_id varchar(64) NOT NULL, case_id uuid NOT NULL, feedback_id uuid NOT NULL REFERENCES feedback(id),
 PRIMARY KEY(tenant_id,feedback_id), FOREIGN KEY(tenant_id,case_id) REFERENCES cx_case(tenant_id,id) ON DELETE CASCADE
);
CREATE TABLE cx_case_activity (
 id uuid PRIMARY KEY, tenant_id varchar(64) NOT NULL, case_id uuid NOT NULL,
 kind varchar(24) NOT NULL, actor varchar(128) NOT NULL, public boolean NOT NULL DEFAULT false,
 text text NOT NULL, created_at timestamptz NOT NULL DEFAULT now(),
 FOREIGN KEY(tenant_id,case_id) REFERENCES cx_case(tenant_id,id) ON DELETE CASCADE
);
CREATE INDEX cx_case_activity_order ON cx_case_activity(tenant_id,case_id,created_at,id);
CREATE TABLE cx_case_attachment (
 id uuid PRIMARY KEY, tenant_id varchar(64) NOT NULL, case_id uuid NOT NULL,
 filename varchar(256) NOT NULL, object_key text NOT NULL, content_type varchar(128) NOT NULL,
 size_bytes bigint NOT NULL CHECK(size_bytes BETWEEN 1 AND 10485760), public boolean NOT NULL,
 uploaded_by varchar(128) NOT NULL, created_at timestamptz NOT NULL DEFAULT now(),
 FOREIGN KEY(tenant_id,case_id) REFERENCES cx_case(tenant_id,id) ON DELETE CASCADE
);
CREATE TABLE cx_notification (
 id uuid PRIMARY KEY, tenant_id varchar(64) NOT NULL, audience varchar(16) NOT NULL CHECK(audience IN ('staff','customer')),
 recipient varchar(128) NOT NULL, title varchar(256) NOT NULL, body text NOT NULL, link text NOT NULL,
 dedup_key varchar(256) NOT NULL, created_at timestamptz NOT NULL DEFAULT now(), read_at timestamptz,
 UNIQUE(tenant_id,audience,recipient,dedup_key)
);
CREATE INDEX cx_notification_inbox ON cx_notification(tenant_id,audience,recipient,created_at DESC);
CREATE TABLE cx_automation_rule (
 id uuid PRIMARY KEY, tenant_id varchar(64) NOT NULL, name varchar(128) NOT NULL,
 topic varchar(128), severity varchar(16), channel varchar(64), repeat_count integer NOT NULL DEFAULT 1 CHECK(repeat_count BETWEEN 1 AND 1000),
 priority varchar(16) NOT NULL CHECK(priority IN ('critical','high','medium','low')),
 team_id uuid, assignee_id uuid REFERENCES app_user(id), create_case boolean NOT NULL DEFAULT true,
 notify boolean NOT NULL DEFAULT true, enabled boolean NOT NULL DEFAULT false, created_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(tenant_id,id), FOREIGN KEY(tenant_id,team_id) REFERENCES cx_team(tenant_id,id)
);
CREATE TABLE cx_automation_execution (
 tenant_id varchar(64) NOT NULL, rule_id uuid NOT NULL, feedback_id uuid NOT NULL REFERENCES feedback(id),
 case_id uuid, state varchar(16) NOT NULL DEFAULT 'completed', error text, executed_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(rule_id,feedback_id), FOREIGN KEY(tenant_id,rule_id) REFERENCES cx_automation_rule(tenant_id,id)
);
CREATE TABLE cx_survey_campaign (
 id uuid PRIMARY KEY, tenant_id varchar(64) NOT NULL, name varchar(128) NOT NULL, question varchar(512) NOT NULL,
 metric varchar(8) NOT NULL CHECK(metric IN ('csat','ces','nps')),
 trigger varchar(24) NOT NULL CHECK(trigger IN ('manual','case_resolved','event')),
 event_name varchar(128), segment varchar(128), cooldown_days integer NOT NULL CHECK(cooldown_days BETWEEN 1 AND 365),
 reminder_days integer NOT NULL CHECK(reminder_days BETWEEN 1 AND 30), expires_days integer NOT NULL CHECK(expires_days BETWEEN 1 AND 90),
 enabled boolean NOT NULL DEFAULT false, created_at timestamptz NOT NULL DEFAULT now(), UNIQUE(tenant_id,id)
);
CREATE TABLE cx_survey_invitation (
 id uuid PRIMARY KEY, tenant_id varchar(64) NOT NULL, campaign_id uuid NOT NULL, customer_id varchar(128) NOT NULL,
 source_key varchar(200) NOT NULL, case_id uuid, metric varchar(8) NOT NULL, question varchar(512) NOT NULL,
 sent_at timestamptz NOT NULL DEFAULT now(), expires_at timestamptz NOT NULL, reminder_at timestamptz, answered_at timestamptz,
 score integer, comment text, feedback_id uuid, declined_at timestamptz,
 UNIQUE(tenant_id,campaign_id,source_key), FOREIGN KEY(tenant_id,campaign_id) REFERENCES cx_survey_campaign(tenant_id,id),
 FOREIGN KEY(tenant_id,customer_id) REFERENCES customer_identity(tenant_id,customer_id),
 FOREIGN KEY(tenant_id,case_id) REFERENCES cx_case(tenant_id,id),
 CHECK(score IS NULL OR (metric='csat' AND score BETWEEN 1 AND 5) OR (metric='ces' AND score BETWEEN 1 AND 7) OR (metric='nps' AND score BETWEEN 0 AND 10))
);
CREATE INDEX cx_survey_customer ON cx_survey_invitation(tenant_id,customer_id,sent_at DESC);
CREATE TABLE cx_campaign_event (
 tenant_id varchar(64) NOT NULL, campaign_id uuid NOT NULL, event_id bigint NOT NULL REFERENCES cx_event(id),
 invited boolean NOT NULL, processed_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(campaign_id,event_id),
 FOREIGN KEY(tenant_id,campaign_id) REFERENCES cx_survey_campaign(tenant_id,id)
);
CREATE TABLE cx_journey (
 id uuid PRIMARY KEY, tenant_id varchar(64) NOT NULL, name varchar(128) NOT NULL,
 window_days integer NOT NULL CHECK(window_days BETWEEN 1 AND 365), enabled boolean NOT NULL DEFAULT true,
 UNIQUE(tenant_id,id)
);
CREATE TABLE cx_journey_stage (
 id uuid PRIMARY KEY, tenant_id varchar(64) NOT NULL, journey_id uuid NOT NULL, title varchar(128) NOT NULL,
 position integer NOT NULL CHECK(position>=0), event_names text[] NOT NULL,
 UNIQUE(journey_id,position), FOREIGN KEY(tenant_id,journey_id) REFERENCES cx_journey(tenant_id,id) ON DELETE CASCADE
);
