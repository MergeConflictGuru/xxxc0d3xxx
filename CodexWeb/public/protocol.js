// Completed-turn notifications can contain only a display summary. Keep items
// already received from the stream unless the server explicitly sends full history.
export function mergeTurn(previous, update) {
  if(update.itemsView==='full')return {...previous,...update,items:update.items||[]};
  const items=new Map((previous.items||[]).map(item=>[item.id,item]));
  for(const item of update.items||[])items.set(item.id,item);
  return {...previous,...update,items:[...items.values()]};
}
