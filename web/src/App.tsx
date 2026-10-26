import { Link, Route, Routes } from 'react-router-dom'
import { AuthStatusProvider } from './authStatus'
import { useT } from './i18n'
import KidGate from './kid/KidGate'
import KidHome from './kid/KidHome'
import { KidSessionProvider } from './kid/KidSession'
import ProfilePicker from './kid/ProfilePicker'
import About from './about/About'
import ParentArea from './ParentArea'
import Shell from './Shell'

function NotFound() {
  const t = useT()
  return (
    <>
      <h1>{t('app.notFound')}</h1>
      <Link to="/">{t('app.goToPicker')}</Link>
    </>
  )
}

export default function App() {
  return (
    <AuthStatusProvider>
      <KidSessionProvider>
        <Routes>
          <Route element={<Shell />}>
            <Route
              index
              element={
                <KidGate>
                  <ProfilePicker />
                </KidGate>
              }
            />
            <Route
              path="home"
              element={
                <KidGate>
                  <KidHome />
                </KidGate>
              }
            />
            <Route path="parent" element={<ParentArea />} />
            <Route path="about" element={<About />} />
            <Route path="*" element={<NotFound />} />
          </Route>
        </Routes>
      </KidSessionProvider>
    </AuthStatusProvider>
  )
}
