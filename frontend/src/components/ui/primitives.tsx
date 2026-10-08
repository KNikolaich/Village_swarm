import type { HTMLAttributes, InputHTMLAttributes, SelectHTMLAttributes } from 'react'
import { cn } from '@/lib/utils'

export function Card({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('rounded-xl border border-neutral-800 bg-neutral-900/70', className)} {...props} />
}

export function CardHeader({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('flex items-center justify-between gap-2 px-4 pt-4', className)} {...props} />
}

export function CardTitle({ className, ...props }: HTMLAttributes<HTMLHeadingElement>) {
  return <h2 className={cn('text-sm font-medium text-neutral-400', className)} {...props} />
}

export function CardContent({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('p-4', className)} {...props} />
}

const badgeTones = {
  neutral: 'bg-neutral-800 text-neutral-300',
  info: 'bg-sky-500/15 text-sky-300',
  warn: 'bg-amber-500/15 text-amber-300',
  alarm: 'bg-red-500/20 text-red-300',
  critical: 'bg-red-600 text-white',
  ok: 'bg-emerald-500/15 text-emerald-300',
} as const

export type BadgeTone = keyof typeof badgeTones

export function Badge({ tone = 'neutral', className, ...props }: HTMLAttributes<HTMLSpanElement> & { tone?: BadgeTone }) {
  return (
    <span
      className={cn('inline-flex items-center gap-1 rounded-md px-2 py-0.5 text-xs font-medium', badgeTones[tone], className)}
      {...props}
    />
  )
}

export function Input({ className, ...props }: InputHTMLAttributes<HTMLInputElement>) {
  return (
    <input
      className={cn(
        'h-11 w-full rounded-lg border border-neutral-700 bg-neutral-900 px-3 text-base text-neutral-100 placeholder:text-neutral-500 focus:border-amber-400 focus:outline-none',
        className,
      )}
      {...props}
    />
  )
}

export function Select({ className, ...props }: SelectHTMLAttributes<HTMLSelectElement>) {
  return (
    <select
      className={cn(
        'h-9 rounded-lg border border-neutral-700 bg-neutral-900 px-2 text-sm text-neutral-200 focus:border-amber-400 focus:outline-none',
        className,
      )}
      {...props}
    />
  )
}

export function Skeleton({ className }: { className?: string }) {
  return <div className={cn('animate-pulse rounded-lg bg-neutral-800/80', className)} />
}

export function Empty({ children }: { children: React.ReactNode }) {
  return <p className="py-10 text-center text-sm text-neutral-500">{children}</p>
}
