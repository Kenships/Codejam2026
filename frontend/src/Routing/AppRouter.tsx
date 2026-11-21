import { BrowserRouter, Navigate, Route, Routes } from 'react-router'
import { LoginPage } from '../Pages/LoginPage'

export function AppRouter() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/join" element={<LoginPage />} />
        <Route path="*" element={<Navigate to="/join" replace />} />
      </Routes>
    </BrowserRouter>
  )
}
