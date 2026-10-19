import { Link, Route, Routes } from 'react-router-dom'
import { useT } from './i18n'
import KidHome from './kid/KidHome'
import { KidSessionProvider } from './kid/KidSession'
import ProfilePicker from './kid/ProfilePicker'
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
    <KidSessionProvider>
      <Routes>
        <Route element={<Shell />}>
          <Route index element={<ProfilePicker />} />
          <Route path="home" element={<KidHome />} />
          <Route path="parent" element={<ParentArea />} />
          <Route path="*" element={<NotFound />} />
        </Route>
      </Routes>
    </KidSessionProvider>
  )
}
