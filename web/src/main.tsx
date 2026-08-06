import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { QueryClientProvider } from '@tanstack/react-query'
import { Toaster } from 'sonner'
import { App } from './App'
import { AuthProvider } from './auth/AuthProvider'
import { queryClient } from './api/queryClient'
import './styles.css'

createRoot(document.getElementById('root')!).render(<StrictMode><BrowserRouter><AuthProvider><QueryClientProvider client={queryClient}><App /><Toaster position="top-right" richColors /></QueryClientProvider></AuthProvider></BrowserRouter></StrictMode>)
