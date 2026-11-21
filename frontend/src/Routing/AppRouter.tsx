import { BrowserRouter, Navigate, Route, Routes, useLocation } from 'react-router'
import { LoginPage } from '../Pages/LoginPage'
import { PlayPage } from '../Pages/PlayPage'

/**
 * Unknown paths go to /join, KEEPING the query string, so a QR link like
 * https://<site>/?room=ABCD (or ?ws=... for the backup relay) still works.
 */
function RedirectToJoin() {
  const { search } = useLocation()
  return <Navigate to={{ pathname: '/join', search }} replace />
}

export function AppRouter() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/join" element={<LoginPage />} />
        <Route path="/play/:roomCode" element={<PlayPage />} />
        <Route path="*" element={<RedirectToJoin />} />
      </Routes>
    </BrowserRouter>
  )
}
