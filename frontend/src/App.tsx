import { useState } from 'react'

export function App() {
  const [count, setCount] = useState(0)

  return (
    <main className="flex min-h-screen flex-col items-center justify-center bg-slate-950 p-6 text-slate-100">
      <div className="w-full max-w-md rounded-2xl border border-slate-800 bg-slate-900 p-8 text-center shadow-xl">
        <h1 className="text-3xl font-bold tracking-tight text-white">
          React + TypeScript + Tailwind
        </h1>
        <p className="mt-2 text-sm text-slate-400">
          Edit <code className="rounded bg-slate-800 px-1.5 py-0.5 text-sky-400">src/App.tsx</code> to get started.
        </p>
        <button
          type="button"
          onClick={() => setCount((c) => c + 1)}
          className="mt-6 rounded-lg bg-sky-500 px-4 py-2 font-medium text-slate-950 transition hover:bg-sky-400 active:scale-95"
        >
          Count is {count}
        </button>
      </div>
    </main>
  )
}

export default App
