import { cleanup, render } from '@testing-library/react'
import { afterEach, expect, it } from 'vitest'
import { avatars } from '../kidProfiles'
import { axe } from '../test/axe'
import Avatar from './Avatar'

afterEach(cleanup)

it('offers the six bundled avatars', () => {
  expect(avatars).toEqual(['fox', 'owl', 'bear', 'cat', 'frog', 'panda'])
})

it.each(avatars)('draws %s as an svg', (id) => {
  const { container } = render(<Avatar avatar={id} name="Ada" />)
  expect(container.querySelector('.avatar svg')).not.toBeNull()
  expect(container.textContent).toBe('')
})

it('falls back to the first letter for an unknown avatar', () => {
  const { container } = render(<Avatar avatar="dragon" name="ada" />)
  expect(container.querySelector('svg')).toBeNull()
  expect(container.textContent).toBe('A')
})

it('is hidden from assistive technology and has no violations', async () => {
  const { container } = render(
    <main>
      <h1>Who is watching?</h1>
      {avatars.map((id) => (
        <p key={id}>
          <Avatar avatar={id} name={id} /> {id}
        </p>
      ))}
    </main>,
  )
  expect(
    container.querySelectorAll('.avatar[aria-hidden="true"]'),
  ).toHaveLength(6)
  expect(await axe(container)).toHaveNoViolations()
})
