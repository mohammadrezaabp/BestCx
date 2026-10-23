import { useEffect, useRef, useState } from "react";
import { api, type SessionUser } from "../api";
import { useTheme, type ThemeChoice } from "../hooks/useTheme";
import Icon, { type IconName } from "./Icon";
import Modal from "./Modal";
import NotificationBell from "./NotificationBell";
import { OpsField, useOperation } from "./OperationsCommon";

const themes: Record<ThemeChoice, { icon: IconName; label: string }> = {
  system: { icon: "monitor", label: "System theme" }, light: { icon: "sun", label: "Light mode" }, dark: { icon: "moon", label: "Dark mode" },
};

export default function UserControls({ user, canReadNotifications, onSignOut, onProfileChanged }: {
  user: SessionUser; canReadNotifications: boolean; onSignOut: () => void; onProfileChanged: () => void;
}) {
  const theme = useTheme();
  const [settings, setSettings] = useState(false);
  const menu = useRef<HTMLDetailsElement>(null);
  useEffect(() => {
    const close = (event: PointerEvent | KeyboardEvent) => {
      if (menu.current && (event instanceof KeyboardEvent ? event.key === "Escape" : !menu.current.contains(event.target as Node))) menu.current.open = false;
    };
    window.addEventListener("pointerdown", close); window.addEventListener("keydown", close);
    return () => { window.removeEventListener("pointerdown", close); window.removeEventListener("keydown", close); };
  }, []);
  const openSettings = () => { if (menu.current) menu.current.open = false; setSettings(true); };
  return <>
    {canReadNotifications && <NotificationBell />}
    <button className="icon-button" onClick={theme.cycle} aria-label={themes[theme.choice].label} title={themes[theme.choice].label}><Icon name={themes[theme.choice].icon} /></button>
    <details className="current-user-menu" ref={menu}><summary className="icon-button" aria-label={`Account ${user.display_name || user.username}`} title={user.display_name || user.username}><Icon name="user" /></summary>
      <div className="current-user-popover"><strong>{user.display_name || user.username}</strong><small dir="ltr">{user.username}</small>
        <button onClick={openSettings}><Icon name="settings" size={16} />My account settings</button>
        {user.mode === "login" && <button onClick={onSignOut}><Icon name="logout" size={16} />Sign out</button>}
      </div>
    </details>
    {settings && <ProfileSettings user={user} theme={theme.choice} onTheme={theme.setChoice} onClose={() => setSettings(false)} onSaved={() => { setSettings(false); onProfileChanged(); }} />}
  </>;
}

function ProfileSettings({ user, theme, onTheme, onClose, onSaved }: {
  user: SessionUser; theme: ThemeChoice; onTheme: (value: ThemeChoice) => void; onClose: () => void; onSaved: () => void;
}) {
  const action = useOperation();
  const [name, setName] = useState(user.display_name || user.username);
  return <Modal title="My account settings" onClose={onClose}>
    {action.errorBox}<form onSubmit={event => { event.preventDefault(); void action.run(async () => {
      await api.updateProfile({ display_name: name.trim() });
      onSaved();
    }); }}>
      <fieldset className="ops-message-fields" disabled={action.busy}>
        <OpsField label="Username"><input value={user.username} readOnly dir="ltr" autoComplete="username" /></OpsField>
        <OpsField label="Display name"><input required maxLength={256} value={name} readOnly={user.mode !== "login"} onChange={event => setName(event.target.value)} autoComplete="name" /></OpsField>
        <OpsField label="Appearance of the system"><select value={theme} onChange={event => onTheme(event.target.value as ThemeChoice)}>{Object.entries(themes).map(([value, item]) => <option key={value} value={value}>{item.label}</option>)}</select></OpsField>
        {user.mode !== "login" && <p className="small muted">The profile of this account is managed by the login system. The appearance setting is saved automatically.</p>}
      </fieldset>
      <div className="form-submit">{user.mode === "login" && <button className="primary" disabled={action.busy || !name.trim()}>Save changes</button>}<button type="button" disabled={action.busy} onClick={onClose}>Close</button></div>
    </form>
  </Modal>;
}
