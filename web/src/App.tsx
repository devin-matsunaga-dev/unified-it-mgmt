import { Route, Routes } from 'react-router-dom'
import { ProtectedRoute } from './auth/ProtectedRoute'
import { AppShell } from './layout/AppShell'
import { PortalShell } from './layout/PortalShell'
import { AuthCallbackPage } from './pages/AuthCallbackPage'
import { ForbiddenPage } from './pages/ForbiddenPage'
import { CiDetailPage } from './features/assets/CiDetailPage'
import { CiImportWizard } from './features/assets/CiImportWizard'
import { CiListPage } from './features/assets/CiListPage'
import { ScanPage } from './features/assets/ScanPage'
import { ContractDetailPage } from './features/contracts/ContractDetailPage'
import { ContractListPage } from './features/contracts/ContractListPage'
import { VendorListPage } from './features/contracts/VendorListPage'
import { PeopleListPage } from './features/people/PeopleListPage'
import { UserDetailPage } from './features/people/UserDetailPage'
import { HomeRoute } from './pages/HomeRoute'
import { LoginPage } from './pages/LoginPage'
import { PlaceholderPage } from './pages/PlaceholderPage'
import { MyRequestsPage } from './features/portal/MyRequestsPage'
import { NewRequestPage } from './features/portal/NewRequestPage'
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
      <Route path="requests/:id" element={<RequestDetailPage />} />
    </Route>
    <Route element={<ProtectedRoute><AppShell /></ProtectedRoute>}>
      <Route index element={<HomeRoute />} />
      <Route path="tickets" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><TicketListPage /></ProtectedRoute>} />
      <Route path="tickets/:id" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><TicketDetailPage /></ProtectedRoute>} />
      <Route path="assets" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><CiListPage /></ProtectedRoute>} />
      <Route path="assets/import" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><CiImportWizard /></ProtectedRoute>} />
      <Route path="assets/:id" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><CiDetailPage /></ProtectedRoute>} />
      <Route path="scan" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><ScanPage /></ProtectedRoute>} />
      <Route path="contracts" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><ContractListPage /></ProtectedRoute>} />
      <Route path="contracts/vendors" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><VendorListPage /></ProtectedRoute>} />
      <Route path="contracts/:id" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><ContractDetailPage /></ProtectedRoute>} />
      <Route path="people" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><PeopleListPage /></ProtectedRoute>} />
      <Route path="people/:userId" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><UserDetailPage /></ProtectedRoute>} />
      <Route path="monitoring" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><PlaceholderPage title="Monitoring" /></ProtectedRoute>} />
      <Route path="admin/users" element={<ProtectedRoute roles={['Admin']}><PlaceholderPage title="Users" /></ProtectedRoute>} />
      <Route path="admin/settings" element={<ProtectedRoute roles={['Admin']}><PlaceholderPage title="Settings" /></ProtectedRoute>} />
    </Route>
    <Route path="*" element={<ProtectedRoute><PlaceholderPage title="Page not found" /></ProtectedRoute>} />
  </Routes>
}
