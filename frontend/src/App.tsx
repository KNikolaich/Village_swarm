import ru from '@/i18n/ru.json'

// Placeholder shell. Routing, overview, events and photo archive arrive in build step 7.
export default function App() {
  return (
    <main className="mx-auto flex min-h-dvh max-w-3xl flex-col gap-2 p-4">
      <h1 className="text-2xl font-semibold">{ru.app.title}</h1>
      <p className="text-neutral-400">{ru.app.placeholder}</p>
    </main>
  )
}
