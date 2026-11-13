import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { ApiError } from '@/api/client'
import { type Language, setLanguage } from '@/i18n'
import App from './App.tsx'
import '@/lib/install'
import { loadHiveTitle } from '@/lib/hiveTitle'
import './index.css'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      // Do not hammer the hive on auth/permission errors; retry network trouble (4G at the dacha).
      retry: (count, error) => !(error instanceof ApiError && error.status < 500) && count < 3,
    },
  },
})

// The hive has one interface language (Settings → Interface language). Read it before the first render, so every
// string is in that language from the start; if the hive is unreachable, Russian is the fallback.
async function loadLanguage(): Promise<Language> {
  try {
    const response = await fetch('/api/settings/language')
    const body = (await response.json()) as { language?: Language }
    return body.language === 'en' ? 'en' : 'ru'
  } catch {
    return 'ru'
  }
}

// The sign-in page has no alias from /api/cluster (it needs a session), so the tab is named from the open manifest meanwhile.
void loadHiveTitle().then((title) => {
  if (title) document.title = title
})

void loadLanguage().then((language) => {
  setLanguage(language)
  document.documentElement.lang = language
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <App />
      </QueryClientProvider>
    </StrictMode>,
  )
})
