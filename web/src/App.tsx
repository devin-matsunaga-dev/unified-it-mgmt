import { Route, Routes } from 'react-router-dom'
import { ProtectedRoute } from './auth/ProtectedRoute'
import { AppShell } from './layout/AppShell'
import { PortalShell } from './layout/PortalShell'
import { AuthCallbackPage } from './pages/AuthCallbackPage'
import { ForbiddenPage } from './pages/ForbiddenPage'
import { CiDetailPage } from './features/assets/CiDetailPage'
import { CiImportWizard } from './features/assets/CiImportWizard'
import { CiListPage } from './features/assets/CiListPage'
import { AuditSessionPage } from './features/assets/AuditSessionPage'
import { AuditSessionsPage } from './features/assets/AuditSessionsPage'
import { DiscoveryReviewPage } from './features/assets/DiscoveryReviewPage'
import { DriftReportPage } from './features/assets/DriftReportPage'
import { ScanPage } from './features/assets/ScanPage'
import { ContractDetailPage } from './features/contracts/ContractDetailPage'
import { ContractListPage } from './features/contracts/ContractListPage'
import { VendorListPage } from './features/contracts/VendorListPage'
import { AlertBoardPage } from './features/monitoring/AlertBoardPage'
import { DeviceDetailPage } from './features/monitoring/DeviceDetailPage'
import { StatusBoardPage } from './features/monitoring/StatusBoardPage'
import { PeopleListPage } from './features/people/PeopleListPage'
import { UserDetailPage } from './features/people/UserDetailPage'
import { LicensePoolPage } from './features/software/LicensePoolPage'
import { SoftwareImportPage } from './features/software/SoftwareImportPage'
import { SoftwarePage } from './features/software/SoftwarePage'
import { SoftwareProductPage } from './features/software/SoftwareProductPage'
import { TopologyPage } from './features/topology/TopologyPage'
import { ChangeCalendarPage } from './features/changes/ChangeCalendarPage'
import { ChangeDetailPage } from './features/changes/ChangeDetailPage'
import { HomeRoute } from './pages/HomeRoute'
import { KbArticlePage } from './features/knowledge/KbArticlePage'
import { KbListPage } from './features/knowledge/KbListPage'
import { ProblemListPage } from './features/problems/ProblemListPage'
import { ProblemDetailPage } from './features/problems/ProblemDetailPage'
import { LoginPage } from './pages/LoginPage'
import { PlaceholderPage } from './pages/PlaceholderPage'
import { MyRequestsPage } from './features/portal/MyRequestsPage'
import { NewRequestPage } from './features/portal/NewRequestPage'
import { PortalArticlePage } from './features/portal/PortalArticlePage'
import { PortalKbPage } from './features/portal/PortalKbPage'
import { RequestDetailPage } from './features/portal/RequestDetailPage'
import { TicketDetailPage } from './features/tickets/TicketDetailPage'
import { TicketListPage } from './features/tickets/TicketListPage'

export function App() {
  return <Routes>
    <Route path="/login" element={<LoginPage />} />
    <Route path="/auth/callback" element={<AuthCallbackPage />} />
    <Route path="/auth/silent-callback" element={<AuthCallbackPage silent />} />
    <Route path="/forbidden" element={<ForbiddenPage />} />
    <Route path="portal" element={<ProtectedRoute roles={['EndUser']}><PortalShell /></ProtectedRoute>}>
      <Route index element={<MyRequestsPage />} />
      <Route path="new" element={<NewRequestPage />} />
      <Route path="kb" element={<PortalKbPage />} />
      <Route path="kb/:id" element={<PortalArticlePage />} />
      <Route path="requests/:id" element={<RequestDetailPage />} />
    </Route>
    <Route element={<ProtectedRoute><AppShell /></ProtectedRoute>}>
      <Route index element={<HomeRoute />} />
      <Route path="tickets" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><TicketListPage /></ProtectedRoute>} />
      <Route path="tickets/:id" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><TicketDetailPage /></ProtectedRoute>} />
      <Route path="problems" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><ProblemListPage /></ProtectedRoute>} />
      <Route path="problems/:id" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><ProblemDetailPage /></ProtectedRoute>} />
      <Route path="knowledge" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><KbListPage /></ProtectedRoute>} />
      <Route path="knowledge/:id" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><KbArticlePage /></ProtectedRoute>} />
      <Route path="assets" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><CiListPage /></ProtectedRoute>} />
      <Route path="assets/import" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><CiImportWizard /></ProtectedRoute>} />
      <Route path="assets/discovery" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><DiscoveryReviewPage /></ProtectedRoute>} />
      <Route path="assets/drift" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><DriftReportPage /></ProtectedRoute>} />
      <Route path="assets/:id" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><CiDetailPage /></ProtectedRoute>} />
      <Route path="changes" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><ChangeCalendarPage /></ProtectedRoute>} />
      <Route path="changes/:id" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><ChangeDetailPage /></ProtectedRoute>} />
      <Route path="audits" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><AuditSessionsPage /></ProtectedRoute>} />
      <Route path="audits/:id" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><AuditSessionPage /></ProtectedRoute>} />
      <Route path="topology" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><TopologyPage /></ProtectedRoute>} />
      <Route path="software" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><SoftwarePage /></ProtectedRoute>} />
      <Route path="software/import" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><SoftwareImportPage /></ProtectedRoute>} />
      <Route path="software/licenses" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><LicensePoolPage /></ProtectedRoute>} />
      <Route path="software/products/:id" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><SoftwareProductPage /></ProtectedRoute>} />
      <Route path="scan" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><ScanPage /></ProtectedRoute>} />
      <Route path="contracts" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><ContractListPage /></ProtectedRoute>} />
      <Route path="contracts/vendors" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><VendorListPage /></ProtectedRoute>} />
      <Route path="contracts/:id" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><ContractDetailPage /></ProtectedRoute>} />
      <Route path="people" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><PeopleListPage /></ProtectedRoute>} />
      <Route path="people/:userId" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><UserDetailPage /></ProtectedRoute>} />
      <Route path="monitoring" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><StatusBoardPage /></ProtectedRoute>} />
      <Route path="monitoring/alerts" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><AlertBoardPage /></ProtectedRoute>} />
      <Route path="monitoring/devices/:id" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><DeviceDetailPage /></ProtectedRoute>} />
      <Route path="admin/users" element={<ProtectedRoute roles={['Admin']}><PlaceholderPage title="Users" /></ProtectedRoute>} />
      <Route path="admin/settings" element={<ProtectedRoute roles={['Admin']}><PlaceholderPage title="Settings" /></ProtectedRoute>} />
    </Route>
    <Route path="*" element={<ProtectedRoute><PlaceholderPage title="Page not found" /></ProtectedRoute>} />
  </Routes>
}
