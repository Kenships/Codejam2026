import { BrowserRouter, Navigate, Route, Routes } from 'react-router'
import { LoginPage } from '../Pages/LoginPage'
import { PlayPage } from '../Pages/PlayPage'

export function AppRouter() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/join" element={<LoginPage />} />
        <Route path="/play/:roomCode" element={<PlayPage />} />
        <Route path="*" element={<Navigate to="/join" replace />} />
      </Routes>
    </BrowserRouter>
  )
}
