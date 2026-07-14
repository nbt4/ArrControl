import React from 'react';
import { createRoot } from 'react-dom/client';
import { Activity, AlertTriangle, Download, Film, Search, Settings } from 'lucide-react';
import './styles.css';

const cards = [
  { label: 'Missing', value: '—', hint: 'Across all libraries', icon: Film },
  { label: 'Active queue', value: '—', hint: 'All download clients', icon: Download },
  { label: 'Needs attention', value: '—', hint: 'Imports and health', icon: AlertTriangle },
  { label: 'Online services', value: '—', hint: 'Last provider poll', icon: Activity },
];

function App() {
  return <div className="shell"><aside><div className="brand"><span>AC</span><b>ArrControl</b></div><nav>{['Overview','Library','Missing','Queue','Health','Tasks'].map((x,i)=><a className={i===0?'active':''} key={x}>{x}</a>)}</nav><a className="settings"><Settings size={18}/>Settings</a></aside><main><header><div><p className="eyebrow">OPERATIONS CENTER</p><h1>Good evening.</h1><p className="muted">Your media automation, in one calm place.</p></div><button><Search size={18}/>Global search</button></header><section className="grid">{cards.map(({label,value,hint,icon:Icon})=><article key={label}><div className="icon"><Icon size={20}/></div><p>{label}</p><strong>{value}</strong><small>{hint}</small></article>)}</section><section className="panel"><div><p className="eyebrow">GET STARTED</p><h2>Connect your first service</h2><p className="muted">Add Sonarr, Radarr, or another provider. Credentials are encrypted and never returned by the API.</p></div><button>Add service</button></section></main></div>;
}
createRoot(document.getElementById('root')!).render(<React.StrictMode><App/></React.StrictMode>);
