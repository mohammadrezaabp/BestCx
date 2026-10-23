-- Preserve existing event-based journeys and enrich them with customer experience maps.
ALTER TABLE cx_journey
  ADD COLUMN persona text NOT NULL DEFAULT '',
  ADD COLUMN scenario text NOT NULL DEFAULT '',
  ADD COLUMN goal text NOT NULL DEFAULT '',
  ADD COLUMN map_type varchar(32) NOT NULL DEFAULT 'current_state'
    CHECK (map_type IN ('current_state', 'future_state'));

ALTER TABLE cx_journey_stage
  ADD COLUMN experience jsonb NOT NULL DEFAULT '{}'::jsonb
    CHECK (jsonb_typeof(experience) = 'object');
