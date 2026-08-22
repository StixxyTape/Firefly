alright so now, claude, I just want to go through the whole design before we commit to
anything. 

The plan: Fillion now hooks into incidents and changes what their letters say as they arrive.

Approaches: 
- 1. Wait for the letter to fire. 
	- [Dismissed - unintuitive and clunky] Route A - Let the regular letter fire, then a few seconds later Fillion's narrative rewrite fires. 
	- [Dismissed - extremely punishing] Route B - Dont fire the regular letter. Instead, let the incident play out, but then only fire Fillion's rewrite a few seconds later.
- 2. Wait until the incident has fired, before the letter. 
	- [Dismissed - changes the game's core logic too much] Whenever something is created through Entity.Spawn, AddHediff, and any other similar methods, if it originally came from an incident causing method such as TryFire, TryExecute, TryApply, etc, then we pause all of the event's logic + hide it from the player until the LLM catches up with the rewritten letter.
- 3. Catch the event before it's fired.
	- [Dismissed - leaves out too much information for Fillion] Whenever an Incident is sent into TryExecute, if it's valid, Fillion catches and pauses it to write the description for it's letter. The event is then unpaused and continues as normal, with the letter text changed.
- 4. Write our own event firing logic.
	- [Being considered as it is the cleanest approach, but most mod incompatible as it supersedes the vanilla approach] We write our own Try methods for firing incidents which handle all the event details and planning first before executing. Creating a clear split between events being created, planned, and the letters being written, and then executing, makes it easy for Fillion to hook into any step of this process. Also paves the way for letting Fillion create his own future events.

What we've weighed:
- I believe Approach 2 and 4 are the best for what we want to do. Between these two, it's hard to decide which one to go for. Approach 2 is messier as we are touching core game logic, however it provides some level of mod compatibility for simple mods which hook into the vanilla methods. Approach 2 also presents risks where updates to the base game could mess up our approach. Approach 4 is cleaner as we are overhauling the game's methods with our own new methods, making for a much tidier seam. It does however provide no mod compatibility (unless we figure out a workaround to this) as we need to teach the game to use our methods instead of its base ones. 