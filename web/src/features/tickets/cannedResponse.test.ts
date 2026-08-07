import { insertCannedResponse } from './cannedResponse'

describe('insertCannedResponse', () => {
  it('fills an empty reply box with the response alone', () => {
    expect(insertCannedResponse('', 'Hello Ada.', null)).toEqual({ text: 'Hello Ada.', inserted: 'Hello Ada.' })
  })

  it('replaces the previous insertion when cycling through templates', () => {
    const first = insertCannedResponse('', 'Acknowledged.', null)
    const second = insertCannedResponse(first.text, 'Need more detail.', first.inserted)

    expect(second.text).toBe('Need more detail.')
  })

  it('keeps text the agent typed and swaps only the template after it', () => {
    const first = insertCannedResponse('Quick note:', 'Acknowledged.', null)
    expect(first.text).toBe('Quick note:\n\nAcknowledged.')

    const second = insertCannedResponse(first.text, 'Need more detail.', first.inserted)
    expect(second.text).toBe('Quick note:\n\nNeed more detail.')
  })

  it('appends instead of replacing once the insertion has been edited', () => {
    const first = insertCannedResponse('', 'Acknowledged.', null)
    const edited = `${first.text} Ticket is mine.`

    expect(insertCannedResponse(edited, 'Need more detail.', first.inserted).text)
      .toBe('Acknowledged. Ticket is mine.\n\nNeed more detail.')
  })
})
