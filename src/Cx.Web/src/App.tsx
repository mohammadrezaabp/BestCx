import { lazy, Suspense, useEffect, useRef, useState } from "react";
import { Link, NavLink, Navigate, useParams, Route, Routes, useLocation } from "react-router-dom";
import { api, type SessionUser } from "./api";
import { ErrorBox, Loading, PageHead, useAsync } from "./components/Common";
import Icon, { type IconName } from "./components/Icon";
import UserControls from "./components/UserControls";
import { PageChrome } from "./components/PageChrome";
import { voicePath } from "./workspace";
import { useSession } from "./hooks/useSession";
import Login from "./pages/Login";
import Overview from "./pages/Overview";
const Customers = lazy(() => import("./pages/Customers"));
const Customer360 = lazy(() => import("./pages/Customer360"));
const Inbox = lazy(() => import("./pages/Inbox"));
const Reports = lazy(() => import("./pages/Reports"));
const VoiceCalls = lazy(() => import("./pages/VoiceCalls"));
const VoiceReview = lazy(() => import("./pages/VoiceReview"));
const Taxonomy = lazy(() => import("./pages/Taxonomy"));
const PipelineHealth = lazy(() => import("./pages/PipelineHealth"));
const AuditLog = lazy(() => import("./pages/AuditLog"));
const Cases = lazy(() => import("./pages/Cases"));
const CaseDetail = lazy(() => import("./pages/CaseDetail"));
const OperationsSettings = lazy(() => import("./pages/OperationsSettings"));
const Problems = lazy(() => import("./pages/Problems"));
const Campaigns = lazy(() => import("./pages/Campaigns"));
const Journeys = lazy(() => import("./pages/JourneyMap"));
const ServiceOutcomes = lazy(() => import("./pages/ServiceOutcomes"));
const FollowUpCustomers = lazy(() => import("./pages/FollowUpCustomers"));
const Notifications = lazy(() => import("./pages/Notifications"));
type NavigationItem = { to: string; label: string; icon: IconName; permission?: string };
const complaintNavigation: NavigationItem[] = [
  { to: "/complaints", label: "Complaint inbox", icon: "inbox" },
  { to: "/complaints/reports", label: "Complaint reports", icon: "chart" },
  { to: "/complaints/voice", label: "Voice and calls", icon: "voice" },
  { to: "/complaints/problems", label: "Shared problems", icon: "layers", permission: "cx.work.read" },
  { to: "/complaints/cases", label: "Cases", icon: "briefcase", permission: "cx.work.read" },
  { to: "/complaints/follow-up-customers", label: "Follow-up customers", icon: "followup", permission: "cx.work.read" },
  { to: "/complaints/outcomes", label: "Service outcomes", icon: "outcome", permission: "cx.work.read" },
  { to: "/complaints/settings", label: "Case settings", icon: "settings", permission: "cx.operations.configure" },
];
const experienceNavigation: NavigationItem[] = [
  { to: "/experience", label: "Feedback inbox", icon: "inbox" },
  { to: "/experience/reports", label: "Experience reports", icon: "chart" },
  { to: "/experience/campaigns", label: "Survey campaigns", icon: "campaign", permission: "cx.operations.configure" },
  { to: "/experience/journeys", label: "Customer journey", icon: "journey", permission: "cx.work.read" },
  { to: "/experience/voice", label: "Voice and calls", icon: "voice" },
];
const workspaceNavigation: { key: string; label: string; icon: IconName; items: NavigationItem[] }[] = [
  { key: "complaints", label: "Complaints", icon: "alert", items: complaintNavigation },
  { key: "experience", label: "Customer experience", icon: "spark", items: experienceNavigation },
];
const sharedNavigation: NavigationItem[] = [
  { to: "/customers", label: "Customers", icon: "users" },
  { to: "/taxonomy", label: "Topic categories", icon: "tree" },
  { to: "/pipelines", label: "Processing status", icon: "settings" },
  { to: "/audit", label: "Activity log", icon: "clock" },
];
export default function App() {
  const { state, signIn, signOut, refresh } = useSession();

  if (state.status === "loading")
    return (
      <div className="app-splash">
        <span className="brand-mark">
          <i />
          <i />
          <i />
        </span>
      </div>
    );
  if (state.status === "signed-out") return <Login onSignedIn={signIn} error={state.error} />;
  return <Shell user={state.user} onSignOut={signOut} onProfileChanged={refresh} />;
}

function Shell({ user, onSignOut, onProfileChanged }: { user: SessionUser; onSignOut: () => void; onProfileChanged: () => void }) {
  const can = (permission: string) => user.permissions.includes("cx.admin") || user.permissions.includes(permission);
  const work = can("cx.work.read");
  const configure = can("cx.operations.configure");
  const manageWork = can("cx.work.manage");
  const manageTeams = can("cx.team.manage");
  const [menuOpen, setMenuOpen] = useState(false);
  const sidebarRef = useRef<HTMLElement>(null);
  const mainRef = useRef<HTMLDivElement>(null);
  const menuButtonRef = useRef<HTMLButtonElement>(null);
  const location = useLocation();
  const current = [...complaintNavigation, ...experienceNavigation, ...sharedNavigation, { to: "/notifications", label: "Notifications" }]
    .sort((a, b) => b.to.length - a.to.length)
    .find(item => location.pathname === item.to || location.pathname.startsWith(`${item.to}/`));
  useEffect(() => {
    setMenuOpen(false);
    document.title = `${current?.label ?? (location.pathname === "/" ? "Overview" : "Details")} | BestCx`;
  }, [location.pathname, current?.label]);
  useEffect(() => {
    const close = (event: KeyboardEvent) => {
      if (event.key === "Escape") setMenuOpen(false);
    };
    window.addEventListener("keydown", close);
    return () => window.removeEventListener("keydown", close);
  }, []);
  useEffect(() => {
    if (!menuOpen) return;
    const shell = mainRef.current;
    if (shell) shell.inert = true;
    sidebarRef.current?.querySelector<HTMLElement>("a")?.focus();
    const resize = () => {
      if (window.innerWidth > 960) setMenuOpen(false);
    };
    window.addEventListener("resize", resize);
    return () => {
      if (shell) shell.inert = false;
      window.removeEventListener("resize", resize);
      menuButtonRef.current?.focus();
    };
  }, [menuOpen]);
  return (
    <div className="app">
      <a href="#page-content" className="skip-link">
        Go to page content
      </a>
      {menuOpen && (
        <button
          className="nav-backdrop"
          aria-label="Close the menu"
          onClick={() => setMenuOpen(false)}
        />
      )}
      <aside
        ref={sidebarRef}
        onKeyDown={(event) => {
          if (!menuOpen || event.key !== "Tab") return;
          const targets = [
            ...(sidebarRef.current?.querySelectorAll<HTMLElement>(
              "a, button, summary",
            ) ?? []),
          ].filter((node) => node.getClientRects().length > 0);
          const first = targets[0],
            last = targets[targets.length - 1];
          if (event.shiftKey && document.activeElement === first) {
            event.preventDefault();
            last?.focus();
          } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first?.focus();
          }
        }}
        className={`sidebar ${menuOpen ? "is-open" : ""}`}
        id="main-navigation"
      >
        <Link className="brand" to="/">
          <span className="brand-mark">
            <i />
            <i />
            <i />
          </span>
          <span>BestCx</span>
        </Link>
        <nav className="nav" aria-label="Main navigation">
          <NavLink to="/" end><Icon name="overview" /><span>Overview</span></NavLink>
          {workspaceNavigation.map(({ key, label, icon, items }) =>
            <details key={key} className="admin-nav workspace-nav" open={location.pathname.startsWith(`/${key}`) || undefined}>
              <summary><Icon name={icon} /><span>{label}</span><Icon name="chevron" size={15} /></summary>
              {items.filter(item => !item.permission || can(item.permission)).map(item => <NavLink key={item.to} to={item.to} end={item.to === `/${key}`}>
                <Icon name={item.icon} size={16} /><span>{item.label}</span>
              </NavLink>)}
            </details>)}
          <div className="nav-shared">{sharedNavigation.map(item => <NavLink key={item.to} to={item.to}><Icon name={item.icon!} /><span>{item.label}</span></NavLink>)}</div>
        </nav>
      </aside>
      <div className="main-shell" ref={mainRef}>
        <PageChrome.Provider value={<div className="page-account-actions">
          <button ref={menuButtonRef} className="icon-button mobile-menu" aria-label="Open the menu" aria-expanded={menuOpen} aria-controls="main-navigation" onClick={() => setMenuOpen(value => !value)}><Icon name="menu" /></button>
          <UserControls user={user} canReadNotifications={work} onSignOut={onSignOut} onProfileChanged={onProfileChanged} />
        </div>}>
        <main className="main" id="page-content" tabIndex={-1}>
          <Suspense fallback={<Loading />}>
            <Routes>
              <Route path="/" element={<Overview />} />
              <Route path="/customers" element={<Customers />} />
              <Route path="/customers/:customerId" element={<Customer360 />} />
              <Route path="/complaints" element={<Inbox key="complaints" inbox="complaints" />} />
              <Route path="/complaints/reports" element={<Reports key="complaint-reports" inbox="complaints" />} />
              <Route path="/complaints/voice" element={<VoiceCalls key="complaint-calls" inbox="complaints" />} />
              <Route path="/complaints/voice/:callId" element={<VoiceReview inbox="complaints" />} />
              <Route path="/complaints/cases" element={<Cases canRecordComplaint={user.permissions.includes("cx.admin")} />} />
              <Route path="/complaints/cases/:caseId" element={<CaseDetail manager={configure} canManage={manageWork} />} />
              <Route path="/complaints/problems" element={<Problems manager={configure} />} />
              <Route path="/complaints/follow-up-customers" element={<FollowUpCustomers />} />
              <Route path="/complaints/outcomes" element={<ServiceOutcomes />} />
              <Route path="/complaints/settings" element={<OperationsSettings canManageTeam={manageTeams} />} />
              <Route path="/experience" element={<Inbox key="feedback" inbox="feedback" />} />
              <Route path="/experience/reports" element={<Reports key="experience-reports" inbox="feedback" />} />
              <Route path="/experience/campaigns" element={<Campaigns />} />
              <Route path="/experience/journeys" element={<Journeys manager={configure} />} />
              <Route path="/experience/voice" element={<VoiceCalls key="experience-calls" inbox="feedback" />} />
              <Route path="/experience/voice/:callId" element={<VoiceReview inbox="feedback" />} />
              <Route path="/inbox" element={<LegacyRedirect to="/experience" />} />
              <Route path="/reports" element={<LegacyRedirect to="/experience/reports" />} />
              <Route path="/voice" element={<LegacyRedirect to="/experience/voice" />} />
              <Route path="/voice/:callId" element={<LegacyVoiceRedirect />} />
              <Route path="/cases" element={<LegacyRedirect to="/complaints/cases" />} />
              <Route path="/cases/:caseId" element={<LegacyCaseRedirect />} />
              <Route path="/problems" element={<LegacyRedirect to="/complaints/problems" />} />
              <Route path="/follow-up-customers" element={<LegacyRedirect to="/complaints/follow-up-customers" />} />
              <Route path="/outcomes" element={<LegacyRedirect to="/complaints/outcomes" />} />
              <Route path="/operations-settings" element={<LegacyRedirect to="/complaints/settings" />} />
              <Route path="/campaigns" element={<LegacyRedirect to="/experience/campaigns" />} />
              <Route path="/journeys" element={<LegacyRedirect to="/experience/journeys" />} />
              <Route path="/taxonomy" element={<Taxonomy />} />
              <Route path="/pipelines" element={<PipelineHealth />} />
              <Route path="/audit" element={<AuditLog />} />
              <Route path="/notifications" element={<Notifications />} />
              <Route
                path="*"
                element={
                  <div><PageHead title="Page not found" /> <Link to="/">Back to overview</Link>
                  </div>
                }
              />
            </Routes>
          </Suspense>
        </main>
        </PageChrome.Provider>
      </div>
    </div>
  );
}

function LegacyRedirect({ to }: { to: string }) {
  const location = useLocation();
  return <Navigate to={`${to}${location.search}${location.hash}`} state={location.state} replace />;
}
function LegacyCaseRedirect() {
  const { caseId = "" } = useParams();
  return <LegacyRedirect to={`/complaints/cases/${encodeURIComponent(caseId)}`} />;
}
function LegacyVoiceRedirect() {
  const { callId = "" } = useParams();
  const detail = useAsync(() => api.voiceCall(callId), [callId]);
  if (detail.loading) return <Loading />;
  if (!detail.data) return <ErrorBox error={detail.error} />;
  return <LegacyRedirect to={voicePath(detail.data.call.inbox, callId)} />;
}
