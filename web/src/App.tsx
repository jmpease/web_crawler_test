import { NavLink, Route, Routes } from 'react-router-dom';
import { StartPage } from './pages/StartPage';
import { JobPage } from './pages/JobPage';
import { HistoryPage } from './pages/HistoryPage';

export function App() {
  return (
    <>
      <header className="app-header">
        <span className="brand">Job Crawler</span>
        <nav>
          <NavLink to="/" end>
            New crawl
          </NavLink>
          <NavLink to="/history">History</NavLink>
        </nav>
      </header>
      <main>
        <Routes>
          <Route path="/" element={<StartPage />} />
          <Route path="/jobs/:id" element={<JobPage />} />
          <Route path="/history" element={<HistoryPage />} />
          <Route path="*" element={<p>Page not found.</p>} />
        </Routes>
      </main>
    </>
  );
}
