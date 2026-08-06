import { Route, Routes } from 'react-router-dom'
import { ProtectedRoute } from './auth/ProtectedRoute'
import { AppShell } from './layout/AppShell'
import { AuthCallbackPage } from './pages/AuthCallbackPage'
import { DashboardPage } from './pages/DashboardPage'
import { ForbiddenPage } from './pages/ForbiddenPage'
import { LoginPage } from './pages/LoginPage'
import { PlaceholderPage } from './pages/PlaceholderPage'

export function App() {
  return <Routes>
    <Route path="/login" element={<LoginPage />} />
    <Route path="/auth/callback" element={<AuthCallbackPage />} />
    <Route path="/auth/silent-callback" element={<AuthCallbackPage silent />} />
    <Route path="/forbidden" element={<ForbiddenPage />} />
    <Route element={<ProtectedRoute><AppShell /></ProtectedRoute>}>
      <Route index element={<DashboardPage />} />
      <Route path="tickets" element={<PlaceholderPage title="Tickets" />} />
      <Route path="assets" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><PlaceholderPage title="Assets" /></ProtectedRoute>} />
      <Route path="monitoring" element={<ProtectedRoute roles={['Admin', 'Technician', 'Manager']}><PlaceholderPage title="Monitoring" /></ProtectedRoute>} />
      <Route path="admin/users" element={<ProtectedRoute roles={['Admin']}><PlaceholderPage title="Users" /></ProtectedRoute>} />
      <Route path="admin/settings" element={<ProtectedRoute roles={['Admin']}><PlaceholderPage title="Settings" /></ProtectedRoute>} />
    </Route>
    <Route path="*" element={<ProtectedRoute><PlaceholderPage title="Page not found" /></ProtectedRoute>} />
  </Routes>
}
